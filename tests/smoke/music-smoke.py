#!/usr/bin/env python3
# Music channel end to end on an isolated server prepared by smoke.py: search, album page data,
# access control, and that playback hands the client the catalog's FLAC byte for byte.
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
CATALOG = 'https://tracks.monochrome.st/track/'


def request(path, token, data=None, headers=None, raw=False):
    req = urllib.request.Request(args.url + path, data=json.dumps(data).encode() if data is not None else None,
                                 headers={'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-music", Version="0.1.0"'
                                          + (', Token="' + token + '"' if token else ''), 'Content-Type': 'application/json', **(headers or {})})
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            body = res.read()
            if raw:
                return res.status, res.headers, body
            return res.status, json.loads(body) if body and res.headers.get_content_type() == 'application/json' else body
    except urllib.error.HTTPError as error:
        return (error.code, error.headers, b'') if raw else (error.code, None)


def ok(path, token, data=None):
    status, body = request(path, token, data)
    assert status < 400, (path, status)
    return body


admin = state['admin']
viewer = state['users']['viewer']
other = state['users']['other']
config = ok(config_path, admin)
config.update({'Enabled': True, 'MusicEnabled': True, 'MusicApiUrl': '', 'CacheRevision': config.get('CacheRevision', 0) + 1})
ok(config_path, admin, config)
for _ in range(20):
    channels = ok('/Channels?UserId=' + viewer['id'], viewer['token'])['Items']
    if any(c['Name'] == 'Music' for c in channels):
        break
    time.sleep(1)
channel = next(c for c in channels if c['Name'] == 'Music')
assert not any(c['Name'] == 'Music' for c in ok('/Channels?UserId=' + other['id'], other['token'])['Items'])
print('PASS: viewer sees Music, other user does not', flush=True)

base = '/Items?userId=' + viewer['id'] + '&recursive=true&limit=20&searchTerm=daft%20punk%20discovery'
albums = ok(base + '&includeItemTypes=MusicAlbum', viewer['token'])['Items']
album = next(a for a in albums if a['Name'] == 'Discovery')
assert album['Type'] == 'MusicAlbum', album['Type']
songs = ok(base + '&includeItemTypes=Audio', viewer['token'])['Items']
assert songs and all(s['Type'] == 'Audio' for s in songs), songs
print('PASS: search finds albums typed as albums, and songs', flush=True)

tracks = ok('/Items?userId=' + viewer['id'] + '&parentId=' + album['Id'] + '&fields=ProviderIds', viewer['token'])['Items']
assert [t['IndexNumber'] for t in tracks[:3]] == [1, 2, 3] and tracks[0]['Name'] == 'One More Time', [(t.get('IndexNumber'), t['Name']) for t in tracks]
assert tracks[0]['RunTimeTicks'] > 300 * 10_000_000 and tracks[0]['ProviderIds'].get('ISRC'), tracks[0]
detail = ok('/Users/' + viewer['id'] + '/Items/' + album['Id'] + '?fields=ChildCount', viewer['token'])
assert detail['Type'] == 'MusicAlbum' and detail.get('ChildCount', 0) > 0, detail
print('PASS: album lists numbered tracks with durations and ISRCs, and reports a child count', flush=True)

for identity in (other['token'], None):
    for path in ('/Items/' + tracks[0]['Id'], '/Items/' + tracks[0]['Id'] + '/PlaybackInfo', '/Items?ParentId=' + album['Id']):
        status, _ = request(path, identity)
        assert status in (401, 403, 404), (path, status)
print('PASS: other and anonymous users cannot reach music items or playback', flush=True)

profile = {'MaxStreamingBitrate': 140000000, 'DirectPlayProfiles': [{'Container': 'flac', 'Type': 'Audio'}, {'Container': 'mp3', 'Type': 'Audio'}],
           'TranscodingProfiles': [{'Container': 'mp3', 'Type': 'Audio', 'AudioCodec': 'mp3', 'Context': 'Streaming', 'Protocol': 'http'}]}
info = ok('/Items/' + tracks[0]['Id'] + '/PlaybackInfo?UserId=' + viewer['id'], viewer['token'], {'DeviceProfile': profile, 'UserId': viewer['id']})
source = info['MediaSources'][0]
audio = [s for s in source['MediaStreams'] if s['Type'] == 'Audio']
assert source['SupportsDirectPlay'] and source['Container'] == 'flac' and audio[0]['Codec'] == 'flac' and audio[0]['SampleRate'] >= 44100, source
print('PASS: playback offers direct play of lossless FLAC', flush=True)

stream = '/Audio/' + tracks[0]['Id'] + '/stream?static=true&mediaSourceId=' + source['Id']
status, headers, body = request(stream, viewer['token'], headers={'Range': 'bytes=0-65535'}, raw=True)
assert status == 206 and headers.get_content_type() == 'audio/flac' and body[:4] == b'fLaC' and len(body) == 65536, (status, headers.get('Content-Type'), body[:8])
track_id = tracks[0]['ProviderIds']['Custom'].rsplit('-', 1)[1]
upstream = urllib.request.urlopen(urllib.request.Request(CATALOG + track_id, headers={'User-Agent': 'Mozilla/5.0', 'Range': 'bytes=0-65535'}), timeout=60).read()
assert body == upstream, 'the client did not receive the catalog bytes unchanged'
status, headers, body = request(stream, viewer['token'], headers={'Range': 'bytes=30000000-30065535'}, raw=True)
assert status == 206 and len(body) == 65536, status
print('PASS: static stream is the catalog FLAC byte for byte, labelled audio/flac, and seekable', flush=True)

status, _, _ = request(stream, other['token'], raw=True)
assert status in (401, 403, 404), status
recent = next(f for f in ok('/Items?ParentId=' + channel['Id'] + '&UserId=' + viewer['id'], viewer['token'])['Items'] if f['Name'] == 'Recently played')
assert any(t['Name'] == 'One More Time' for t in ok('/Items?ParentId=' + recent['Id'] + '&UserId=' + viewer['id'], viewer['token'])['Items'])
print('PASS: other user cannot stream, and the play appears in Recently played', flush=True)

# History is per user: with access of their own, the other user still sees none of the viewer's plays,
# not in their Recently played, not by opening the viewer's folder directly, and not through search.
allowed = ok(config_path, admin)
ok(config_path, admin, {**allowed, 'AllowedUserIds': [viewer['id'], other['id']], 'CacheRevision': allowed.get('CacheRevision', 0) + 1})
try:
    theirs = next(f for f in ok('/Items?ParentId=' + channel['Id'] + '&UserId=' + other['id'], other['token'])['Items'] if f['Name'] == 'Recently played')
    for folder in {theirs['Id'], recent['Id']}:
        assert not ok('/Items?ParentId=' + folder + '&UserId=' + other['id'], other['token'])['Items'], folder
    found = ok('/Items?Recursive=true&SearchTerm=One%20More%20Time&IncludeItemTypes=Audio&UserId=' + other['id'], other['token'])['Items']
    assert not any(i.get('ParentId') == recent['Id'] for i in found), found
    print('PASS: Recently played is private to each user', flush=True)
finally:
    ok(config_path, admin, {**allowed, 'CacheRevision': allowed.get('CacheRevision', 0) + 2})
