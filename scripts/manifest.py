"""Add a release to manifest.json, the plugin repository Jellyfin reads under Dashboard > Plugins > Repositories."""
import argparse
import datetime
import hashlib
import json
import pathlib
import zipfile

root = pathlib.Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--zip', type=pathlib.Path, required=True)
parser.add_argument('--url', required=True, help='download URL of the release ZIP')
parser.add_argument('--changelog', default='')
args = parser.parse_args()

with zipfile.ZipFile(args.zip) as package:
    meta = json.loads(package.read('meta.json'))
path = root / 'manifest.json'
plugins = json.loads(path.read_text()) if path.exists() else []
plugin = next((p for p in plugins if p['guid'] == meta['guid']), None)
if plugin is None:
    plugin = {'guid': meta['guid'], 'versions': []}
    plugins.append(plugin)
plugin.update(
    name=meta['name'], description=meta['description'], overview=meta['overview'],
    owner=meta['owner'], category=meta['category'],
    imageUrl='https://raw.githubusercontent.com/mmrmagno/jellyfin-plugin-streamfin/main/assets/banner.png',
)
plugin['versions'] = [v for v in plugin['versions'] if v['version'] != meta['version']]
plugin['versions'].insert(0, {
    'version': meta['version'],
    'changelog': args.changelog.strip() or f"StreamFin {meta['version']}",
    'targetAbi': meta['targetAbi'],
    'sourceUrl': args.url,
    'checksum': hashlib.md5(args.zip.read_bytes()).hexdigest(),
    'timestamp': datetime.datetime.now(datetime.timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
})
path.write_text(json.dumps(plugins, indent=2) + '\n')
print(f"manifest.json now lists {len(plugin['versions'])} version(s), newest {meta['version']}")
