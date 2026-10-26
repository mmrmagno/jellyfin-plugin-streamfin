#!/usr/bin/env python3
"""Upgrade and HLS checks against a disposable Jellyfin instance; never point this at production.

--phase before: run with AniFin 0.4 installed. Bootstraps the server, records watch progress.
--phase after:  run after installing StreamFin 0.5. Checks progress and settings survived,
                then plays HiAnime through Jellyfin's own FFmpeg, seeks, and reads subtitles.
"""
import argparse
import json
import os
import pathlib
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request

PLUGIN = 'a74fdb2e-938f-483b-b2ef-f527e14ff724'
parser = argparse.ArgumentParser()
parser.add_argument('--url', required=True)
parser.add_argument('--phase', choices=['before', 'after'], required=True)
parser.add_argument('--state', default='.test-state/upgrade.secret')
args = parser.parse_args()
state_path = pathlib.Path(args.state)


def request(path, token=None, data=None, method=None, raw=False, limit=None):
    headers = {'Content-Type': 'application/json', 'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Upgrade", DeviceId="streamfin-upgrade", Version="0.5.0"'}
    if token:
        headers['Authorization'] += ', Token="' + token + '"'
    req = urllib.request.Request(args.url + path, headers=headers, data=json.dumps(data).encode() if data is not None else None, method=method)
    try:
        with urllib.request.urlopen(req, timeout=180) as res:
            body = res.read(limit) if limit else res.read()
            if raw:
                return res.status, body
            return res.status, json.loads(body) if body and res.headers.get_content_type() == 'application/json' else body.decode(errors='replace')
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode(errors='replace')[:300]


def ok(path, token=None, data=None, method=None, **kw):
    status, body = request(path, token, data, method, **kw)
    if status >= 400:
        raise RuntimeError(f'{path.split("?")[0]} returned HTTP {status}: {body}')
    return body


def save(state):
    state_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    fd = os.open(state_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, 'w') as file:
        json.dump(state, file)


def children(parent, user):
    return ok('/Items?ParentId=' + parent + '&UserId=' + user['id'] + '&Fields=Overview,ProviderIds,Tags', user['token'])['Items']


def search(term, user):
    return ok('/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm=' + urllib.parse.quote(term) + '&UserId=' + user['id'], user['token'])['Items']


def refresh_channels(admin):
    task = next((t for t in ok('/ScheduledTasks', admin) if 'channel' in t['Name'].lower()), None)
    if task and task['State'] != 'Running':
        ok('/ScheduledTasks/Running/' + task['Id'], admin, method='POST')


def channel(name, user):
    for _ in range(30):
        found = [c for c in ok('/Channels?UserId=' + user['id'], user['token'])['Items'] if c['Name'] == name]
        if found:
            return found[0]
        time.sleep(1)
    raise RuntimeError(name + ' channel did not appear')


if args.phase == 'before':
    info = ok('/System/Info/Public')
    if info['StartupWizardCompleted']:
        raise SystemExit('Refusing to bootstrap an initialized server.')
    password = secrets.token_urlsafe(24)
    ok('/Startup/Configuration', data={'ServerName': 'StreamFin isolated test', 'UICulture': 'en-US', 'MetadataCountryCode': 'CH', 'PreferredMetadataLanguage': 'en'})
    ok('/Startup/User')
    ok('/Startup/User', data={'Name': 'streamfin-test-admin', 'Password': password})
    ok('/Startup/RemoteAccess', data={'EnableRemoteAccess': True, 'EnableAutomaticPortMapping': False})
    ok('/Startup/Complete', data={})
    admin = ok('/Users/AuthenticateByName', data={'Username': 'streamfin-test-admin', 'Pw': password})['AccessToken']
    state = {'admin': admin, 'users': {}}
    save(state)
    for name in ('marc', 'other'):
        user_password = secrets.token_urlsafe(24)
        ok('/Users/New', admin, {'Name': name, 'Password': user_password})
        session = ok('/Users/AuthenticateByName', data={'Username': name, 'Pw': user_password})
        state['users'][name] = {'id': session['User']['Id'], 'token': session['AccessToken']}
        save(state)
    marc = state['users']['marc']
    plugin = next(p for p in ok('/Plugins', admin) if p['Id'].replace('-', '') == PLUGIN.replace('-', ''))
    assert plugin['Name'] == 'AniFin' and plugin['Version'] == '0.4.0.0', plugin
    ok('/Plugins/' + PLUGIN + '/Configuration', admin, {
        'Enabled': True, 'AllowedUserIds': [marc['id']], 'AnimeGgEnabled': False, 'AnimeHeavenEnabled': True,
        'HideAdultContent': True, 'HideEcchiContent': False, 'MetadataCacheMinutes': 45, 'CacheRevision': int(time.time())})
    refresh_channels(admin)
    channel('Anime', marc)
    series = next(x for x in search('Cowboy Bebop', marc) if x['Name'] == 'Cowboy Bebop')
    seasons = children(series['Id'], marc)
    heaven = next(x for x in seasons if x['Name'] == 'Episodes · AnimeHeaven')
    episode = children(heaven['Id'], marc)[2]
    ok('/UserItems/' + episode['Id'] + '/UserData?UserId=' + marc['id'], marc['token'], {'Played': False, 'PlaybackPositionTicks': 7200000000, 'PlayCount': 1, 'IsFavorite': True})
    state.update(series=series['Id'], season=heaven['Id'], episode=episode['Id'], progress=ok('/UserItems/' + episode['Id'] + '/UserData?UserId=' + marc['id'], marc['token']))
    save(state)
    print('PASS: AniFin 0.4 bootstrapped, progress recorded on ' + episode['Name'], flush=True)
    raise SystemExit(0)

state = json.loads(state_path.read_text())
admin, marc, other = state['admin'], state['users']['marc'], state['users']['other']
assert ok('/System/Info/Public')['ServerName'] == 'StreamFin isolated test'
plugin = next(p for p in ok('/Plugins', admin) if p['Id'].replace('-', '') == PLUGIN.replace('-', ''))
assert plugin['Name'] == 'StreamFin' and plugin['Version'] == '0.5.0.0' and plugin['Status'] == 'Active', plugin
print('PASS: StreamFin 0.5 replaced AniFin 0.4 under the same plugin ID', flush=True)
config = ok('/Plugins/' + PLUGIN + '/Configuration', admin)
assert config['AllowedUserIds'] == [marc['id']] and config['AnimeGgEnabled'] is False and config['HideEcchiContent'] is False and config['MetadataCacheMinutes'] == 45, config
assert config['ZokoEnabled'] is True and config['FilmEnabled'] is True and config['TmdbApiKey'] == '', config
print('PASS: settings carried over, new switches default on', flush=True)
refresh_channels(admin)
anime = channel('Anime', marc)
assert not any(c['Name'] == 'Anime' for c in ok('/Channels?UserId=' + other['id'], other['token'])['Items'])
assert not any(c['Name'] == 'Movies & TV' for c in ok('/Channels?UserId=' + marc['id'], marc['token'])['Items'])
print('PASS: Anime still restricted to marc; Movies & TV hidden without a TMDB key', flush=True)
after = ok('/UserItems/' + state['episode'] + '/UserData?UserId=' + marc['id'], marc['token'])
for key in ('Played', 'PlaybackPositionTicks', 'PlayCount', 'IsFavorite'):
    assert after[key] == state['progress'][key], (key, after[key], state['progress'][key])
seasons = children(state['series'], marc)
print('Provider choices:', ', '.join(s['Name'] for s in seasons), flush=True)
assert any(s['Id'] == state['season'] for s in seasons), 'AnimeHeaven folder ID changed'
assert state['episode'] in {e['Id'] for e in children(state['season'], marc)}, 'AnimeHeaven episode ID changed'
print('PASS: episode IDs, resume position and favourite survived the upgrade', flush=True)

hianime = next(s for s in seasons if s['Name'] == 'Episodes · HiAnime')
episodes = children(hianime['Id'], marc)
assert len(episodes) == 26 and all(e['Name'].endswith('Episode ' + e['Name'].split(' · ')[0]) for e in episodes), [e['Name'] for e in episodes[:3]]
episode = episodes[0]
for token in (other['token'], None):
    assert request('/Items/' + episode['Id'] + '/PlaybackInfo', token)[0] in (401, 403, 404)
info = ok('/Items/' + episode['Id'] + '/PlaybackInfo?UserId=' + marc['id'], marc['token'], {'UserId': marc['id']}, method='POST')
source = info['MediaSources'][0]
kinds = [(s['Type'], s.get('Language')) for s in source['MediaStreams']]
print('HiAnime source:', source['Name'], source['Container'], kinds, round(source['RunTimeTicks'] / 600000000), 'min', flush=True)
assert '/StreamFin/Internal/Stream/' in source['Path'] and source['Path'].endswith('/master.m3u8')
assert ('Video', None) in [(t, None) for t, _ in kinds] and any(t == 'Audio' for t, _ in kinds) and any(t == 'Subtitle' for t, _ in kinds), kinds
assert 20 * 600000000 < source['RunTimeTicks'] < 30 * 600000000
print('PASS: HiAnime resolves to HLS with video, audio and a subtitle track, restricted to marc', flush=True)


def transcode(start_ticks):
    query = urllib.parse.urlencode({
        'MediaSourceId': source['Id'], 'PlaySessionId': secrets.token_hex(8), 'VideoCodec': 'h264', 'AudioCodec': 'aac',
        'VideoBitrate': 2000000, 'AudioBitrate': 128000, 'MaxWidth': 640, 'StartTimeTicks': start_ticks, 'ApiKey': marc['token']})
    started = time.time()
    status, body = request('/Videos/' + episode['Id'] + '/stream.ts?' + query, raw=True, limit=2 * 1024 * 1024)
    assert status == 200 and len(body) > 256 * 1024 and body[0] == 0x47, (status, len(body), body[:40])
    return time.time() - started


print(f'PASS: Jellyfin transcodes HiAnime from the start ({transcode(0):.1f}s for 2 MB)', flush=True)
print(f'PASS: seeking to 12:00 transcodes from the middle ({transcode(12 * 60 * 10_000_000):.1f}s for 2 MB)', flush=True)
subtitle = next(s for s in source['MediaStreams'] if s['Type'] == 'Subtitle')
assert subtitle['IsExternal'] and subtitle['Language'] == 'en' and subtitle['IsDefault'], subtitle
status, body = request(f"/Videos/{episode['Id']}/{source['Id']}/Subtitles/{subtitle['Index']}/0/Stream.vtt?ApiKey={marc['token']}")
assert status == 200 and 'WEBVTT' in body and '-->' in body, (status, body[:200])
print(f"PASS: Jellyfin extracts the English subtitles ({body.count('-->')} cues)", flush=True)

started = time.time()
for i in range(60):
    search(f'zz{secrets.token_hex(3)} {i}', marc)
print(f'60 uncached searches took {time.time() - started:.0f}s', flush=True)
listing = children(anime['Id'], marc)
popular = children(next(f for f in listing if f['Name'] == 'Popular')['Id'], marc)
assert len(popular) > 10 and not any('rate limit' in t['Name'].lower() or 'HTTP 429' in t['Name'] for t in popular), [t['Name'] for t in popular[:5]]
print('PASS: catalog still answers after a burst of searches, no 429 status folders', flush=True)
print('Upgrade smoke checks passed.', flush=True)
