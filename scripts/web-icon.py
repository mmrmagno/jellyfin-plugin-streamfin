#!/usr/bin/env python3
"""Apply/remove a scoped Jellyfin Web sidebar icon, preserving existing branding CSS."""
import argparse
import json
import pathlib
import re
import sqlite3
import urllib.request
import uuid
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--config', type=pathlib.Path, required=True)
parser.add_argument('--user', required=True, help='administrator whose session is used')
parser.add_argument('--icon', type=pathlib.Path)
parser.add_argument('--channel', default='Anime', help='channel name, e.g. Anime or "Movies & TV"')
parser.add_argument('--channel-id', help='channel ID when the administrator is not permitted to browse it')
parser.add_argument('--remove', action='store_true')
args = parser.parse_args()
db = sqlite3.connect(f'file:{args.config / "data/jellyfin.db"}?mode=ro', uri=True)
row = db.execute('SELECT AccessToken FROM Devices JOIN Users ON Users.Id=Devices.UserId WHERE Username=? ORDER BY DateLastActivity DESC LIMIT 1', (args.user,)).fetchone()
db.close()
if not row:
    raise SystemExit('Sign in as the chosen administrator first.')

def api(path, data=None):
    request = urllib.request.Request(args.url.rstrip('/') + path,
        headers={'Authorization': 'MediaBrowser Token="' + row[0] + '"', 'Content-Type': 'application/json'},
        data=json.dumps(data).encode() if data is not None else None)
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read()
        return json.loads(raw) if raw else None

options = api('/System/Configuration/branding')
css = options.get('CustomCss') or ''
# AniFin's old rule goes on any run; StreamFin rules are per channel.
css = re.sub(r'/\* AniFin icon start \*/.*?/\* AniFin icon end \*/\s*', '', css, flags=re.S)
found = args.channel_id or next((c['Id'] for c in api('/Channels')['Items'] if c['Name'] == args.channel), None)
channel_id = uuid.UUID(found).hex if found else None
if channel_id:
    css = re.sub(r'/\* StreamFin icon ' + channel_id + r' start \*/.*?/\* StreamFin icon ' + channel_id + r' end \*/\s*', '', css, flags=re.S)
if not args.remove:
    if not args.icon:
        raise SystemExit('--icon is required when applying the icon.')
    if not channel_id:
        raise SystemExit('Channel ' + args.channel + ' not found; pass --channel-id.')
    # A CSS clip path needs no image fetch and works with a strict img-src CSP.
    # The distributed icon uses a 24x24 viewBox and a single vector path.
    icon = ET.fromstring(args.icon.read_bytes())
    paths = [node.attrib['d'] for node in icon.iter() if node.tag.endswith('}path')]
    if icon.attrib.get('viewBox') != '0 0 24 24' or len(paths) != 1 or not re.fullmatch(r'[A-Za-z0-9., +\-]+', paths[0]):
        raise SystemExit('Expected a distributed 24x24 single-path icon (shuriken.svg or popcorn.svg).')
    modern = 'a[href*="parentId=' + channel_id + '"] .MuiButton-startIcon'
    css += '\n/* StreamFin icon ' + channel_id + ' start */\n' + '[data-itemid="' + channel_id + '"] .navMenuOptionIcon,\n' + modern + ' {\n' \
        + '  font-size: 0 !important; width: 24px; height: 24px; flex-shrink: 0; background: currentColor;\n' \
        + '  clip-path: path(' + json.dumps(paths[0]) + ');\n' \
        + '}\n' + modern + ' svg { visibility: hidden; }\n/* StreamFin icon ' + channel_id + ' end */\n'
options['CustomCss'] = css
api('/System/Configuration/branding', options)
print('StreamFin sidebar icon for ' + args.channel + ' ' + ('removed.' if args.remove else 'applied; refresh the browser.'))
