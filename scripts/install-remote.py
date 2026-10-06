#!/usr/bin/env python3
"""Run as root on the Docker host after staging the release ZIP. Stops only Jellyfin."""
import argparse
import datetime
import json
import os
import pathlib
import shutil
import sqlite3
import subprocess
import tarfile
import time
import xml.etree.ElementTree as ET
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--config', type=pathlib.Path, required=True)
parser.add_argument('--artifact', type=pathlib.Path, required=True)
parser.add_argument('--backups', type=pathlib.Path, required=True)
parser.add_argument('--user', default='marc')
args = parser.parse_args()
if os.geteuid() != 0:
    raise SystemExit('Run as root to preserve Jellyfin configuration ownership.')
os.umask(0o077)


def docker(*values):
    return subprocess.check_output(['docker', *values], text=True).strip()


info = json.loads(docker('exec', 'jellyfin', 'curl', '-fsS', 'http://localhost:8096/System/Info/Public'))
if info['Version'] != '12.1.0':
    raise SystemExit('This build was verified on Jellyfin 12.1.0; refusing a different server version.')
connection = sqlite3.connect(f'file:{args.config / "data/jellyfin.db"}?mode=ro', uri=True)
rows = connection.execute('SELECT Id FROM Users WHERE Username = ?', (args.user,)).fetchall()
connection.close()
if len(rows) != 1:
    raise SystemExit('The requested Jellyfin account must exist and be unambiguous.')
user_id = rows[0][0]
with zipfile.ZipFile(args.artifact) as package:
    expected = {'Jellyfin.Plugin.AniFin.dll', 'meta.json', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'APACHE-2.0.txt'}
    if set(package.namelist()) != expected:
        raise SystemExit('Unexpected release archive contents.')
    files = {name: package.read(name) for name in expected}
metadata = json.loads(files['meta.json'])
if metadata['guid'] != 'a74fdb2e-938f-483b-b2ef-f527e14ff724' or metadata['targetAbi'] != '12.1.0.0':
    raise SystemExit('Release metadata does not match AniFin for Jellyfin 12.1.')

stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
backup = args.backups / stamp
backup.mkdir(parents=True, mode=0o700)
plugin = args.config / 'plugins' / ('AniFin_' + metadata['version'])
settings = args.config / 'plugins/configurations/Jellyfin.Plugin.AniFin.xml'
previous_settings = settings.read_bytes() if settings.exists() else None
previous_plugins = []
installed = False
stopped = False
try:
    print('Stopping Jellyfin for a consistent configuration backup.', flush=True)
    docker('stop', 'jellyfin')
    stopped = True
    with tarfile.open(backup / 'jellyfin-config.tar.gz', 'w:gz', compresslevel=1) as archive:
        archive.add(args.config, arcname='config')
    for manifest in (args.config / 'plugins').glob('*/meta.json'):
        if json.loads(manifest.read_text()).get('guid') == metadata['guid']:
            old = manifest.parent
            destination = backup / old.name
            shutil.move(old, destination)
            previous_plugins.append((old, destination))
    plugin.mkdir(parents=True)
    installed = True
    for name, content in files.items():
        path = plugin / name
        path.write_bytes(content)
        path.chmod(0o644)
        os.chown(path, 1500, 1500)
    os.chown(plugin, 1500, 1500)
    plugin.chmod(0o755)
    configuration = ET.fromstring(previous_settings) if previous_settings else ET.Element('Configuration')
    def setting(name, value, preserve=False):
        node = configuration.find(name)
        if node is None:
            node = ET.SubElement(configuration, name)
        elif preserve:
            return
        node.text = value
    setting('Enabled', 'true')
    allowed = configuration.find('AllowedUserIds')
    if allowed is not None:
        configuration.remove(allowed)
    ET.SubElement(ET.SubElement(configuration, 'AllowedUserIds'), 'string').text = user_id
    setting('AnimeGgEnabled', 'true', preserve=True)
    setting('HideAdultContent', 'true', preserve=True)
    setting('HideEcchiContent', 'true', preserve=True)
    setting('MetadataCacheMinutes', '60', preserve=True)
    setting('CacheRevision', str(int(time.time())))
    settings.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(configuration).write(settings, encoding='utf-8', xml_declaration=True)
    os.chown(settings, 1500, 1500)
    docker('start', 'jellyfin')
    stopped = False
    for _ in range(30):
        check = subprocess.run(['docker', 'exec', 'jellyfin', 'curl', '-fsS', '--max-time', '3', 'http://localhost:8096/health'], capture_output=True)
        if check.returncode == 0:
            print('Jellyfin is healthy. AniFin enabled only for ' + args.user + '.', flush=True)
            print('Backup: ' + str(backup / 'jellyfin-config.tar.gz'), flush=True)
            break
        time.sleep(2)
    else:
        raise RuntimeError('Jellyfin did not become healthy after installation.')
except Exception:
    if installed:
        docker('stop', 'jellyfin')
        stopped = True
        shutil.rmtree(plugin)
    for old, destination in previous_plugins:
        shutil.move(destination, old)
    if installed:
        if previous_settings is None:
            settings.unlink(missing_ok=True)
        else:
            settings.write_bytes(previous_settings)
            os.chown(settings, 1500, 1500)
    raise
finally:
    if stopped:
        docker('start', 'jellyfin')
