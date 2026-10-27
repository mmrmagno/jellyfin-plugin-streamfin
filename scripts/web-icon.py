#!/usr/bin/env python3
import argparse
import json
import os
import pathlib
import re
import urllib.request
import xml.etree.ElementTree as ET

assets = pathlib.Path(__file__).resolve().parents[1] / 'src' / 'Assets'
icons = {'Anime': assets / 'shuriken.svg', 'Movies & TV': assets / 'popcorn.svg', 'Music': assets / 'music.svg'}
channels = {'Anime': '18907d884630f40589cbfa4e8ebe228b', 'Movies & TV': '72298e672549e7f9b77425f096e48590', 'Music': 'ce63d2796012584383d21a52e86376c2'}
parser = argparse.ArgumentParser(description='Show the channel icon in the Jellyfin web sidebar. Reads an API key from JELLYFIN_TOKEN.')
parser.add_argument('--url', required=True)
parser.add_argument('--channel', default='Anime', choices=list(icons))
parser.add_argument('--remove', action='store_true')
args = parser.parse_args()
token = os.environ.get('JELLYFIN_TOKEN')
if not token:
    raise SystemExit('Set JELLYFIN_TOKEN to an API key from Dashboard > API Keys.')

def api(path, data=None):
    request = urllib.request.Request(args.url.rstrip('/') + path,
        headers={'Authorization': 'MediaBrowser Token="' + token + '"', 'Content-Type': 'application/json'},
        data=json.dumps(data).encode() if data is not None else None)
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

options = api('/System/Configuration/branding')
css = options.get('CustomCss') or ''
css = re.sub(r'/\* AniFin icon start \*/.*?/\* AniFin icon end \*/\s*', '', css, flags=re.S)
channel_id = channels[args.channel]
css = re.sub(r'/\* StreamFin icon ' + channel_id + r' start \*/.*?/\* StreamFin icon ' + channel_id + r' end \*/\s*', '', css, flags=re.S)
if not args.remove:
    icon = ET.fromstring(icons[args.channel].read_bytes())
    paths = [node.attrib['d'] for node in icon.iter() if node.tag.endswith('}path')]
    if icon.attrib.get('viewBox') != '0 0 24 24' or len(paths) != 1 or not re.fullmatch(r'[A-Za-z0-9., +\-]+', paths[0]):
        raise SystemExit('Expected a 24x24 single-path icon.')
    modern = 'a[href*="parentId=' + channel_id + '"] .MuiButton-startIcon'
    css += '\n/* StreamFin icon ' + channel_id + ' start */\n' + '[data-itemid="' + channel_id + '"] .navMenuOptionIcon,\n' + modern + ' {\n' \
        + '  font-size: 0 !important; width: 24px; height: 24px; flex-shrink: 0; background: currentColor;\n' \
        + '  clip-path: path(' + json.dumps(paths[0]) + ');\n' \
        + '}\n' + modern + ' svg { visibility: hidden; }\n/* StreamFin icon ' + channel_id + ' end */\n'
options['CustomCss'] = css
api('/System/Configuration/branding', options)
print('StreamFin sidebar icon for ' + args.channel + ' ' + ('removed.' if args.remove else 'applied; refresh the browser.'))
