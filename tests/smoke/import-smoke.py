#!/usr/bin/env python3
# Playlist import access on an isolated server prepared by smoke.py: the page holds no data, every call needs a signed-in
# user StreamFin is on for, each user only sees and changes their own imports and playlists, and switching import off
# removes the page.
import argparse
import json
import pathlib
import time
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--state', default='.test-state/session.secret')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'


def request(path, token=None, data=None, client=True, method=None, extra=None, full=False):
    headers = {'Content-Type': 'application/json', **(extra or {})}
    if client:
        headers['Authorization'] = 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-import", Version="0.1.0"' + (', Token="' + token + '"' if token else '')
    req = urllib.request.Request(args.url + path, data=json.dumps(data).encode() if data is not None else None, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            body = res.read()
            if full:
                return res.status, res.headers, body
            return res.status, json.loads(body) if body and res.headers.get_content_type() == 'application/json' else body
    except urllib.error.HTTPError as error:
        return (error.code, error.headers, b'') if full else (error.code, None)


admin = state['admin']
viewer, other = state['users']['viewer'], state['users']['other']
status, config = request(config_path, admin)
original = dict(config)
admin_id = request('/Users/Me', admin)[1]['Id'].replace('-', '')
config.update({'Enabled': True, 'MusicEnabled': True, 'PlaylistImport': True,
               'AllowedUserIds': sorted(set(config.get('AllowedUserIds') or []) - {other['id']} | {viewer['id'], admin_id})})
assert request(config_path, admin, config)[0] < 400
try:
    status, page = request('/StreamFin/Import', client=False)
    assert status == 200 and b'sign in' in page.lower() and viewer['id'].encode() not in page, 'the page is plain HTML with no user data'
    for path, data in [('/StreamFin/Import/Start', {'Text': 'Queen - Bohemian Rhapsody'}), ('/StreamFin/Import/Playlists', None),
                       ('/StreamFin/Import/Export', {'Text': '{}'}), ('/StreamFin/Import/Jobs/abc', None)]:
        assert request(path, data=data, client=False)[0] == 401, ('anonymous', path)
        assert request(path, '0' * 32, data)[0] == 401, ('made-up token', path)
    assert request('/StreamFin/Import/Playlists', other['token'])[0] == 403, 'a user StreamFin is not on for is refused'
    assert request('/StreamFin/Import/Start', other['token'], {'Text': 'Queen - Bohemian Rhapsody'})[0] == 403
    print('PASS: every import call needs a signed-in user StreamFin is on for; the page holds no data', flush=True)

    for name in ('api_key', 'ApiKey'):
        assert request('/StreamFin/Import/Playlists?%s=%s' % (name, viewer['token']), client=False)[0] in (400, 401), 'a token in the address is refused'
    assert request('/StreamFin/Import/Playlists', viewer['token'], extra={'Sec-Fetch-Site': 'cross-site'})[0] == 403, 'a call another site started is refused'
    status, headers, _ = request('/StreamFin/Import/Playlists', viewer['token'], full=True)
    assert status == 200 and headers['Cache-Control'] == 'no-store', 'playlist answers are not kept'
    status, headers, _ = request('/StreamFin/Import', client=False, full=True)
    for name, value in [('X-Frame-Options', 'DENY'), ('Cross-Origin-Opener-Policy', 'same-origin'), ('Referrer-Policy', 'no-referrer'), ('X-Robots-Tag', 'noindex, nofollow')]:
        assert headers[name] == value, (name, headers[name])
    assert "frame-ancestors 'none'" in headers['Content-Security-Policy'] and "script-src 'nonce-" in headers['Content-Security-Policy']
    print('PASS: tokens only in the header, no calls started by other sites, and the page is locked down by its headers', flush=True)

    status, started = request('/StreamFin/Import/Start', viewer['token'], {'Text': 'Queen - Bohemian Rhapsody', 'Name': 'Import smoke'})
    assert status == 200, status
    for _ in range(30):
        status, job = request('/StreamFin/Import/Jobs/' + started['Id'], viewer['token'])
        if job['State'] in ('done', 'failed'):
            break
        time.sleep(2)
    assert job['State'] == 'done' and job['Playlist'], job
    # The admin is another allowed user; it must not reach the viewer's job or playlist.
    assert request('/StreamFin/Import/Jobs/' + started['Id'], admin)[0] == 404, 'another user cannot read the job'
    mine = request('/StreamFin/Import/Playlists', viewer['token'])[1]['Playlists']
    theirs = request('/StreamFin/Import/Playlists', admin)[1]['Playlists']
    assert any(p['Id'] == job['Playlist'] for p in mine) and not any(p['Id'] == job['Playlist'] for p in theirs), 'the playlist belongs to the importer only'
    assert request('/StreamFin/Import/Playlists/%s/Reimport' % job['Playlist'], admin, method='POST')[0] == 400, 'another user cannot re-import it'
    print('PASS: an import lands in the signed-in user\'s playlists, and no other user can see or change it', flush=True)

    off = dict(config, PlaylistImport=False)
    assert request(config_path, admin, off)[0] < 400
    assert request('/StreamFin/Import', client=False)[0] == 404, 'the page is gone while import is off'
    assert request('/StreamFin/Import/Playlists', viewer['token'])[0] == 200 and request('/StreamFin/Import/Start', viewer['token'], {'Text': 'Queen - Bohemian Rhapsody'})[0] == 400
    print('PASS: switching import off removes the page and refuses new imports', flush=True)
finally:
    request(config_path, admin, original)
