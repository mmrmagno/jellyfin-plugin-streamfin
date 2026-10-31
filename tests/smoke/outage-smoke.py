#!/usr/bin/env python3
# Music while the catalog is down, on an isolated server prepared by smoke.py. Two phases:
#   --phase warm     with the catalog reachable: open one album, play one song whole (the only full
#                    download), star and scrobble it, fetch its lyrics and cover. Notes the ids in --memo.
#   --phase outage   after restarting the same container with the catalog unreachable, for example
#                    `docker run ... --add-host tracks.monochrome.st:192.0.2.1` (TEST-NET-1: connections
#                    hang, as during a real outage). Everything warmed must answer at once from what the
#                    server kept on disk, and the stream must be the same bytes; something new must fail
#                    fast with a readable message instead of a raw HTTP status.
#   --youtube        with --phase outage: turn on the YouTube fallback first. Songs of the warmed album
#                    that never played must then play as AAC from YouTube, through Subsonic and Jellyfin,
#                    well within the minute a player waits.
import argparse
import hashlib
import json
import pathlib
import secrets
import time
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--state', default='.test-state/session.secret')
parser.add_argument('--memo', default='.test-state/outage.json')
parser.add_argument('--phase', choices=['warm', 'outage'], required=True)
parser.add_argument('--youtube', action='store_true')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
memo_path = pathlib.Path(args.memo)
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'
REST = args.url + '/StreamFin/rest/'


def jellyfin(path, token, data=None, raw=False, headers=None, method=None):
    req = urllib.request.Request(args.url + path, data=json.dumps(data).encode() if data is not None else None, method=method,
                                 headers={'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-outage", Version="0.1.0", Token="' + token + '"',
                                          'Content-Type': 'application/json', **(headers or {})})
    with urllib.request.urlopen(req, timeout=60) as res:
        body = res.read()
        return (res.status, res.headers, body) if raw else json.loads(body) if body else None


# Each phase sets a fresh app password through the admin API and signs in with it as an API key, so no
# password is ever written to the memo file.
def sign_in():
    password = secrets.token_hex(12)
    config = jellyfin(config_path, admin)
    config.update({'Enabled': True, 'MusicEnabled': True, 'MusicApiUrl': '', 'AllowedUserIds': [viewer['id']], 'SubsonicEnabled': True,
                   'SubsonicPasswords': [{'UserId': viewer['id'], 'Password': password}], 'MusicYouTube': args.youtube, 'MusicAllowLossy': True})
    jellyfin(config_path, admin, config)
    return {'apiKey': password, 'v': '1.16.1', 'c': 'smoke', 'f': 'json'}


def call(method, auth, raw=False, **params):
    query = urllib.parse.urlencode({**auth, **params}, doseq=True)
    with urllib.request.urlopen(REST + method + '.view?' + query, timeout=120) as res:
        body = res.read()
        return (res.status, res.headers, body) if raw else json.loads(body)['subsonic-response']


# A slow catalog gets "answering slowly, try again" while the lookup carries on and is cached; apps retry.
def patient(method, auth, **params):
    for _ in range(8):
        answer = call(method, auth, **params)
        message = answer.get('error', {}).get('message', '')
        if answer['status'] == 'ok' or not ('slowly' in message or 'not responding' in message):
            return answer
        time.sleep(20)
    return answer


def timed(limit, what, method, auth, **params):
    started = time.monotonic()
    answer = call(method, auth, **params)
    took = time.monotonic() - started
    assert took < limit, f'{what} took {took:.1f} s'
    return answer


admin = state['admin']
viewer = state['users']['viewer']
me = sign_in()

if args.phase == 'warm':
    songs = patient('search3', me, query='daft punk one more time', songCount=5, albumCount=0, artistCount=0)['searchResult3']['song']
    song = next(s for s in songs if s.get('duration', 0) > 60)
    album = patient('getAlbum', me, id=song['albumId'])['album']
    assert any(s['id'] == song['id'] for s in album['song'])
    status, headers, body = call('stream', me, raw=True, id=song['id'], format='raw')
    assert status == 200 and body[:4] == b'fLaC' and len(body) == int(headers['Content-Length'])
    assert call('scrobble', me, id=song['id'])['status'] == 'ok'
    assert call('star', me, id=song['id'])['status'] == 'ok'
    lyrics = patient('getLyricsBySongId', me, id=song['id'])['lyricsList'].get('structuredLyrics', [])
    status, headers, cover = call('getCoverArt', me, raw=True, id=song['albumId'])
    assert status == 200 and headers.get_content_type().startswith('image/')
    # Jellyfin keeps the channel's albums by its own ids; this one is opened again during the outage.
    found = jellyfin('/Items?userId=' + viewer['id'] + '&recursive=true&includeItemTypes=MusicAlbum&searchTerm=' + urllib.parse.quote(album['name']), viewer['token'])['Items']
    jellyfin_album = next((a['Id'] for a in found if a['Name'] == album['name']), None)
    # The cache renames the finished download a moment after the last byte is read.
    time.sleep(5)
    memo_path.write_text(json.dumps({'song': song['id'], 'album': song['albumId'], 'title': song['title'], 'jellyfin_album': jellyfin_album,
                                     'others': [s['id'] for s in album['song'] if s['id'] != song['id'] and s.get('duration', 0) > 60][:3],
                                     'sha256': hashlib.sha256(body).hexdigest(), 'lyrics': bool(lyrics), 'cover': hashlib.sha256(cover).hexdigest()}))
    print(f'PASS: warmed one album, one whole song ({len(body) // 1024} KB), its star, scrobble, lyrics and cover', flush=True)
else:
    memo = json.loads(memo_path.read_text())
    assert call('ping', me)['status'] == 'ok'
    album = timed(5, 'getAlbum', 'getAlbum', me, id=memo['album'])['album']
    assert any(s['id'] == memo['song'] for s in album['song'])
    assert timed(5, 'getSong', 'getSong', me, id=memo['song'])['song']['title'] == memo['title']
    print('PASS: an album and a song seen before open at once from the stored copy', flush=True)

    started = time.monotonic()
    status, headers, body = call('stream', me, raw=True, id=memo['song'], format='raw')
    assert status == 200 and hashlib.sha256(body).hexdigest() == memo['sha256'], 'the cached song differs'
    assert time.monotonic() - started < 15, 'the cached song waited on the catalog'
    status, headers, cover = call('getCoverArt', me, raw=True, id=memo['album'])
    assert status == 200 and hashlib.sha256(cover).hexdigest() == memo['cover']
    print('PASS: a cached song streams byte for byte and its cover comes from disk', flush=True)

    assert timed(5, 'scrobble', 'scrobble', me, id=memo['song'])['status'] == 'ok'
    assert timed(5, 'unstar', 'unstar', me, id=memo['song'])['status'] == 'ok'
    assert timed(5, 'star', 'star', me, id=memo['song'])['status'] == 'ok'
    starred = call('getStarred2', me)['starred2'].get('song', [])
    assert any(s['id'] == memo['song'] for s in starred)
    lyrics = timed(30, 'lyrics', 'getLyricsBySongId', me, id=memo['song'])['lyricsList'].get('structuredLyrics', [])
    assert bool(lyrics) == memo['lyrics']
    print('PASS: scrobbles, stars and lyrics work without the catalog', flush=True)

    # Something never seen has to ask the catalog. The first tries wait for the connection to time out; once
    # it counts as down, the answer is immediate. Either way the app gets words, not "HTTP 522".
    for attempt in range(4):
        started = time.monotonic()
        answer = call('search3', me, query='new outage query ' + secrets.token_hex(3))
        took = time.monotonic() - started
        assert answer['status'] == 'failed' and ('not responding' in answer['error']['message'] or 'slowly' in answer['error']['message']), answer
        assert took < 35, f'a search during the outage took {took:.1f} s'
    assert took < 2, f'the catalog was not counted as down after repeated failures ({took:.1f} s)'
    print('PASS: unknown things fail fast with a readable message once the catalog counts as down', flush=True)

    if args.youtube:
        # The tools come from GitHub, which the outage does not touch.
        task = next(t for t in jellyfin('/ScheduledTasks', admin) if t['Key'] == 'StreamFinYtDlp')
        jellyfin('/ScheduledTasks/Running/' + task['Id'], admin, method='POST')
        for _ in range(120):
            task = jellyfin('/ScheduledTasks/' + task['Id'], admin)
            if task['State'] == 'Idle' and task.get('LastExecutionResult', {}).get('Status') in ('Completed', 'Failed'):
                break
            time.sleep(2)
        assert task['LastExecutionResult']['Status'] == 'Completed', task['LastExecutionResult']
        print('PASS: yt-dlp and deno were downloaded, checked against their checksums and start', flush=True)

        fresh = memo['others'][0]
        started = time.monotonic()
        status, headers, body = call('stream', me, raw=True, id=fresh, format='raw')
        took = time.monotonic() - started
        assert status == 200 and headers.get_content_type() == 'audio/mp4' and body[4:8] == b'ftyp' and len(body) > 500_000, (status, headers.get('Content-Type'), body[:64])
        assert took < 45, f'a song from YouTube took {took:.1f} s'
        song = call('getSong', me, id=fresh)['song']
        assert song['suffix'] == 'm4a' and song['contentType'] == 'audio/mp4', song
        print(f'PASS: a song never played before plays from YouTube during the outage ({took:.1f} s), and its details say AAC', flush=True)

        status, headers, body = call('stream', me, raw=True, id=fresh, format='mp3', maxBitRate='128')
        assert status == 200 and headers.get_content_type() == 'audio/mpeg' and len(body) > 100_000, (status, headers.get('Content-Type'))
        print('PASS: a YouTube song converts to MP3 for apps that ask', flush=True)

        tracks = jellyfin('/Items?userId=' + viewer['id'] + '&parentId=' + memo['jellyfin_album'] + '&fields=ProviderIds', viewer['token'])['Items']
        track = next(t for t in tracks if t.get('ProviderIds', {}).get('Custom', '').endswith('-' + memo['others'][1].split('-')[-1]))
        profile = {'MaxStreamingBitrate': 140000000, 'DirectPlayProfiles': [{'Container': 'flac', 'Type': 'Audio'}, {'Container': 'm4a,mp4,mov', 'AudioCodec': 'aac', 'Type': 'Audio'}],
                   'TranscodingProfiles': [{'Container': 'mp3', 'Type': 'Audio', 'AudioCodec': 'mp3', 'Context': 'Streaming', 'Protocol': 'http'}]}
        started = time.monotonic()
        info = jellyfin('/Items/' + track['Id'] + '/PlaybackInfo?UserId=' + viewer['id'], viewer['token'], {'DeviceProfile': profile, 'UserId': viewer['id']})
        took = time.monotonic() - started
        source = info['MediaSources'][0]
        assert took < 50 and 'YouTube' in source['Name'] and source['SupportsDirectPlay'], (took, source['Name'], source.get('Container'))
        stream = '/Audio/' + track['Id'] + '/stream?static=true&mediaSourceId=' + source['Id']
        status, headers, body = jellyfin(stream, viewer['token'], raw=True, headers={'Range': 'bytes=0-65535'})
        assert status == 206 and headers.get_content_type() == 'audio/mp4' and body[4:8] == b'ftyp' and len(body) == 65536, (status, headers.get('Content-Type'), body[:16])
        status, headers, body = jellyfin(stream, viewer['token'], raw=True, headers={'Range': 'bytes=1000000-1065535'})
        assert status == 206 and len(body) == 65536, status
        print(f'PASS: Jellyfin plays a new song from YouTube during the outage ({took:.1f} s to start), labelled AAC and seekable', flush=True)
