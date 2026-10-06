#!/usr/bin/env python3
"""Apply/remove a scoped Jellyfin Web sidebar icon, preserving existing branding CSS."""
import argparse
import base64
import json
import pathlib
import re
import sqlite3
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--config', type=pathlib.Path, required=True)
parser.add_argument('--user', default='marc')
parser.add_argument('--icon', type=pathlib.Path)
parser.add_argument('--channel-id', help='Anime channel ID when the administrator is not permitted to browse it')
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
css = re.sub(r'/\* AniFin icon start \*/.*?/\* AniFin icon end \*/\s*', '', options.get('CustomCss') or '', flags=re.S)
if not args.remove:
    if not args.icon:
        raise SystemExit('--icon is required when applying the icon.')
    channel_id = args.channel_id or next(c['Id'] for c in api('/Channels')['Items'] if c['Name'] == 'Anime')
    import uuid
    channel_id = uuid.UUID(channel_id).hex
    uri = 'data:image/svg+xml;base64,' + base64.b64encode(args.icon.read_bytes()).decode()
    modern = 'a[href*="parentId=' + channel_id + '"] .MuiButton-startIcon'
    css += '\n/* AniFin icon start */\n' + '[data-itemid="' + channel_id + '"] .navMenuOptionIcon,\n' + modern + ' {\n' \
        + '  font-size: 0 !important; width: 1.6rem; height: 1.6rem; background: currentColor;\n' \
        + '  -webkit-mask: url("' + uri + '") center / contain no-repeat;\n' \
        + '  mask: url("' + uri + '") center / contain no-repeat;\n' \
        + '}\n' + modern + ' svg { visibility: hidden; }\n/* AniFin icon end */\n'
options['CustomCss'] = css
api('/System/Configuration/branding', options)
print('AniFin sidebar icon ' + ('removed.' if args.remove else 'applied; refresh the browser.'))
