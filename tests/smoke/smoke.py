#!/usr/bin/env python3
import argparse
import json
import os
import pathlib
import secrets
import time
import urllib.error
import urllib.request

PLUGIN = 'a74fdb2e-938f-483b-b2ef-f527e14ff724'
parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--bootstrap', action='store_true')
parser.add_argument('--state', default='.test-state/session.secret')
args = parser.parse_args()
state_path = pathlib.Path(args.state)


def request(path, token=None, data=None, method=None):
    headers = {'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-smoke", Version="0.1.0"'}
    if token:
        headers['Authorization'] += ', Token="' + token + '"'
    req = urllib.request.Request(args.url + path, headers=headers,
                                 data=json.dumps(data).encode() if data is not None else None, method=method)
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            raw = res.read()
            return res.status, json.loads(raw) if raw and res.headers.get_content_type() == 'application/json' else raw.decode(errors='replace')
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors='replace')[:300]


def ok(path, token=None, data=None, method=None):
    status, body = request(path, token, data, method)
    if status >= 400:
        raise RuntimeError(f'{path.split("?")[0]} returned HTTP {status}: {body}')
    return body


if args.bootstrap:
    info = ok('/System/Info/Public')
    if info['StartupWizardCompleted']:
        raise SystemExit('Refusing to bootstrap an initialized server.')
    password = secrets.token_urlsafe(24)
    ok('/Startup/Configuration', data={'ServerName': 'StreamFin isolated test', 'UICulture': 'en-US', 'MetadataCountryCode': 'CH', 'PreferredMetadataLanguage': 'en'})
    ok('/Startup/User')
    ok('/Startup/User', data={'Name': 'streamfin-test-admin', 'Password': password})
    ok('/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False})
    ok('/Startup/Complete', data={})
    admin = ok('/Users/AuthenticateByName', data={'Username': 'streamfin-test-admin', 'Pw': password})
    token = admin['AccessToken']
    users = {}
    state_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(state_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as file:
        json.dump({'admin': token, 'users': users}, file)
    for name in ('viewer', 'other'):
        user_password = secrets.token_urlsafe(24)
        ok('/Users/New', token, {'Name': name, 'Password': user_password})
        session = ok('/Users/AuthenticateByName', data={'Username': name, 'Pw': user_password})
        users[name] = {'id': session['User']['Id'], 'token': session['AccessToken'], 'password': user_password}
    state = {'admin': token, 'users': users}
    state_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(state_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as file:
        json.dump(state, file)
else:
    state = json.loads(state_path.read_text())

admin = state['admin']
if not state['users']:
    assert ok('/System/Info/Public')['ServerName'] == 'StreamFin isolated test', 'Test account creation is only for the isolated instance.'
    for name in ('viewer', 'other'):
        user_password = secrets.token_urlsafe(24)
        ok('/Users/New', admin, {'Name': name, 'Password': user_password})
        session = ok('/Users/AuthenticateByName', data={'Username': name, 'Pw': user_password})
        state['users'][name] = {'id': session['User']['Id'], 'token': session['AccessToken'], 'password': user_password}
        state_path.write_text(json.dumps(state))
viewer = state['users']['viewer']
other = state['users']['other']
plugins = ok('/Plugins', admin)
assert any(p['Id'].replace('-', '') == PLUGIN.replace('-', '') and p['Status'] == 'Active' for p in plugins), plugins
print('PASS: StreamFin loaded', flush=True)
ok('/Plugins/' + PLUGIN + '/Configuration', admin, {
    'Enabled': True, 'AllowedUserIds': [viewer['id']], 'AnimeGgEnabled': True,
    'MetadataCacheMinutes': 60, 'CacheRevision': int(time.time()),
})
tasks = ok('/ScheduledTasks', admin)
refresh = next((t for t in tasks if 'channel' in t['Name'].lower()), None)
if refresh and refresh['State'] != 'Running':
    ok('/ScheduledTasks/Running/' + refresh['Id'], admin, method='POST')
for _ in range(20):
    channels = ok('/Channels?UserId=' + viewer['id'], viewer['token'])['Items']
    if any(c['Name'] == 'Anime' for c in channels):
        break
    time.sleep(1)
channel = next(c for c in channels if c['Name'] == 'Anime')
print('PASS: viewer can see Anime', flush=True)
assert not any(c['Name'] == 'Anime' for c in ok('/Channels?UserId=' + other['id'], other['token'])['Items'])
print('PASS: other user cannot see Anime', flush=True)
views = ok('/UserViews?UserId=' + viewer['id'], viewer['token'])
assert any(c['Id'] == channel['Id'] for c in views['Items'])
print('PASS: Anime appears among library choices', flush=True)


def children(parent):
    return ok('/Items?ParentId=' + parent + '&UserId=' + viewer['id'] + '&Fields=Overview,ProviderIds', viewer['token'])['Items']


folders = children(channel['Id'])
print('Catalog folders:', ', '.join(f['Name'] for f in folders), flush=True)
popular = next(f for f in folders if f['Name'] == 'Popular')
titles = children(popular['Id'])
print('Catalog results:', ', '.join(t['Name'] for t in titles[:8]), flush=True)
assert len(titles) > 1, 'Catalog failed: ' + str(titles)
title = next((t for t in titles if t['Name'].lower() == 'naruto'), titles[0])
episodes = children(title['Id'])
if episodes and all(e.get('IsFolder') for e in episodes):
    episodes = children(episodes[0]['Id'])
assert episodes and any(not e.get('IsFolder') for e in episodes), 'Source episode listing failed: ' + str(episodes)
episode = next(e for e in episodes if not e.get('IsFolder'))
print('PASS: episodes available for ' + title['Name'], flush=True)
seasons = ok('/Shows/' + title['Id'] + '/Seasons?UserId=' + viewer['id'] + '&Fields=ChildCount', viewer['token'])['Items']
assert seasons and all(s.get('ChildCount', 0) > 0 for s in seasons), [(s['Name'], s.get('ChildCount')) for s in seasons]
print('PASS: seasons report a child count, which Android TV needs to open them', flush=True)
for identity in (other['token'], None):
    for path in ('/Items/' + episode['Id'], '/Items/' + episode['Id'] + '/PlaybackInfo', '/Items?ParentId=' + title['Id']):
        status, _ = request(path, identity)
        assert status in (401, 403, 404), (path, status)
print('PASS: direct catalog/item/playback access denied to other and anonymous users', flush=True)
info = ok('/Items/' + episode['Id'] + '/PlaybackInfo?UserId=' + viewer['id'], viewer['token'])
assert info.get('MediaSources'), info
source = next(s for s in info['MediaSources'] if '/StreamFin/' in s.get('Path', ''))
assert source['MediaStreams'], 'Resolved source lacks media stream information'
assert (source.get('RunTimeTicks') or 0) > 60 * 10_000_000, 'Resolved source is a short clip, not an episode: ' + str(source.get('RunTimeTicks'))
state['episode'] = episode['Id']
state['channel'] = channel['Id']
state['source'] = source
state_path.write_text(json.dumps(state))
print('PASS: fresh stream resolved and probed (' + source['Name'] + ')', flush=True)
all_other = ok('/Items?Recursive=true&IncludeItemTypes=Series,Season,Episode&Fields=Tags', other['token'])['Items']
assert all('StreamFin' not in item.get('Tags', []) for item in all_other)
print('PASS: generic cached item listings do not leak StreamFin to other users', flush=True)
status, _ = request('/Items/' + title['Id'] + '/Images/Primary')
assert status == 200, status
assert request('/Items/' + title['Id'])[0] in (401, 403, 404)
print('PASS: artwork loads without credentials for TV clients; item metadata stays restricted', flush=True)
config_path = '/Plugins/' + PLUGIN + '/Configuration'
config = ok(config_path, admin)
try:
    disabled = dict(config, AllowedUserIds=[])
    ok(config_path, admin, disabled)
    status, _ = request('/Items/' + episode['Id'] + '/PlaybackInfo', viewer['token'])
    assert status in (401, 403, 404), 'Cached media bypassed revoked access'
finally:
    ok(config_path, admin, config)
print('PASS: revocation denies previously cached playback', flush=True)
print('API smoke checks passed.', flush=True)
