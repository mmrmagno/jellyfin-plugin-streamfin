#!/usr/bin/env python3
"""Run as root on the Docker host after staging the release ZIP. Stops only Jellyfin.

Upgrades from AniFin: the old plugin folder and settings file are moved into the backup."""
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
parser.add_argument('--user', required=True, help='Jellyfin user who gets access on a first install')
parser.add_argument('--container', default='jellyfin')
parser.add_argument('--owner', required=True, help='uid:gid of the Jellyfin service user')
args = parser.parse_args()
uid, gid = (int(part) for part in args.owner.split(':'))
if os.geteuid() not in (0, uid):
    raise SystemExit('Run as root (or as the Jellyfin service user) to preserve configuration ownership.')
os.umask(0o077)


def docker(*values):
    return subprocess.check_output(['docker', *values], text=True).strip()


info = json.loads(docker('exec', args.container, 'curl', '-fsS', 'http://localhost:8096/System/Info/Public'))
if info['Version'] != '12.3.0':
    raise SystemExit('This build was verified on Jellyfin 12.3.0; refusing a different server version.')
connection = sqlite3.connect(f'file:{args.config / "data/jellyfin.db"}?mode=ro', uri=True)
rows = connection.execute('SELECT Id FROM Users WHERE Username = ?', (args.user,)).fetchall()
connection.close()
if len(rows) != 1:
    raise SystemExit('The requested Jellyfin account must exist and be unambiguous.')
user_id = rows[0][0]
with zipfile.ZipFile(args.artifact) as package:
    expected = {'Jellyfin.Plugin.StreamFin.dll', 'meta.json', 'streamfin.png', 'LICENSE', 'THIRD_PARTY_NOTICES.txt', 'APACHE-2.0.txt'}
    if set(package.namelist()) != expected:
        raise SystemExit('Unexpected release archive contents.')
    files = {name: package.read(name) for name in expected}
metadata = json.loads(files['meta.json'])
if metadata['guid'] != 'a74fdb2e-938f-483b-b2ef-f527e14ff724' or metadata['targetAbi'] != '12.3.0.0':
    raise SystemExit('Release metadata does not match StreamFin for Jellyfin 12.3.')

stamp = datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')
backup = args.backups / stamp
backup.mkdir(parents=True, mode=0o700)
plugin = args.config / 'plugins' / ('StreamFin_' + metadata['version'])
settings = args.config / 'plugins/configurations/Jellyfin.Plugin.StreamFin.xml'
# Jellyfin names the settings file after the assembly, so the AniFin settings need carrying over once.
legacy_settings = args.config / 'plugins/configurations/Jellyfin.Plugin.AniFin.xml'
previous_settings = settings.read_bytes() if settings.exists() else None
inherited = previous_settings or (legacy_settings.read_bytes() if legacy_settings.exists() else None)
previous_plugins = []
installed = False
stopped = False
try:
    print('Stopping Jellyfin for a consistent configuration backup.', flush=True)
    docker('stop', args.container)
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
        os.chown(path, uid, gid)
    os.chown(plugin, uid, gid)
    plugin.chmod(0o755)
    configuration = ET.fromstring(inherited) if inherited else ET.Element('Configuration')
    def setting(name, value, preserve=False):
        node = configuration.find(name)
        if node is None:
            node = ET.SubElement(configuration, name)
        elif preserve:
            return
        node.text = value
    setting('Enabled', 'true', preserve=True)
    allowed = configuration.find('AllowedUserIds')
    if allowed is None:
        ET.SubElement(ET.SubElement(configuration, 'AllowedUserIds'), 'string').text = user_id
    setting('ZokoEnabled', 'true', preserve=True)
    setting('AnimeGgEnabled', 'true', preserve=True)
    setting('AnimeHeavenEnabled', 'true', preserve=True)
    setting('HideAdultContent', 'true', preserve=True)
    setting('HideEcchiContent', 'true', preserve=True)
    setting('MetadataCacheMinutes', '60', preserve=True)
    setting('FilmEnabled', 'true', preserve=True)
    setting('TmdbApiKey', '', preserve=True)
    setting('CacheRevision', str(int(time.time())))
    settings.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(configuration).write(settings, encoding='utf-8', xml_declaration=True)
    os.chown(settings, uid, gid)
    docker('start', args.container)
    stopped = False
    for _ in range(30):
        check = subprocess.run(['docker', 'exec', args.container, 'curl', '-fsS', '--max-time', '3', 'http://localhost:8096/health'], capture_output=True)
        if check.returncode == 0:
            print('Jellyfin is healthy. Existing StreamFin enablement and access settings preserved; new installations default to ' + args.user + '.', flush=True)
            print('Backup: ' + str(backup / 'jellyfin-config.tar.gz'), flush=True)
            if legacy_settings.exists():
                shutil.move(legacy_settings, backup / legacy_settings.name)
                print('Moved the old AniFin settings file into the backup; StreamFin uses its own copy now.', flush=True)
            break
        time.sleep(2)
    else:
        raise RuntimeError('Jellyfin did not become healthy after installation.')
except Exception:
    if installed:
        docker('stop', args.container)
        stopped = True
        shutil.rmtree(plugin)
    for old, destination in previous_plugins:
        shutil.move(destination, old)
    if installed:
        if previous_settings is None:
            settings.unlink(missing_ok=True)
        else:
            settings.write_bytes(previous_settings)
            os.chown(settings, uid, gid)
    raise
finally:
    if stopped:
        docker('start', args.container)
