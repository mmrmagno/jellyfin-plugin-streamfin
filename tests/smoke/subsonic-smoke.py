#!/usr/bin/env python3
# OpenSubsonic end to end on an isolated server prepared by smoke.py: app password auth, browsing,
# lyrics, stars, playlists, and that stream hands out the catalog's FLAC. Only byte ranges are read,
# so a run costs the catalog a few small requests, not whole songs.
import argparse
import hashlib
import json
import pathlib
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--state', default='.test-state/session.secret')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'
REST = args.url + '/StreamFin/rest/'


def jellyfin(path, token, data=None):
    req = urllib.request.Request(args.url + path, data=json.dumps(data).encode() if data is not None else None,
                                 headers={'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-subsonic", Version="0.1.0", Token="' + token + '"',
                                          'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=60) as res:
        body = res.read()
        return json.loads(body) if body else None


def credentials(user, password):
    salt = secrets.token_hex(8)
    return {'u': user, 't': hashlib.md5((password + salt).encode()).hexdigest(), 's': salt, 'v': '1.16.1', 'c': 'smoke', 'f': 'json'}


def call(method, auth, raw=False, headers=None, limit=None, **params):
    query = urllib.parse.urlencode({**auth, **params}, doseq=True)
    with urllib.request.urlopen(urllib.request.Request(REST + method + '.view?' + query, headers=headers or {}), timeout=120) as res:
        body = res.read(limit) if limit else res.read()
        return (res.status, res.headers, body) if raw else json.loads(body)['subsonic-response']


admin = state['admin']
viewer = state['users']['viewer']
other = state['users']['other']
viewer_name = jellyfin('/Users/' + viewer['id'], admin)['Name']
other_name = jellyfin('/Users/' + other['id'], admin)['Name']
password = secrets.token_hex(12)
other_password = secrets.token_hex(12)
config = jellyfin(config_path, admin)
original = dict(config)
config.update({'Enabled': True, 'MusicEnabled': True, 'MusicApiUrl': '', 'AllowedUserIds': [viewer['id']], 'SubsonicEnabled': True,
               'SubsonicPasswords': [{'UserId': viewer['id'], 'Password': password}, {'UserId': other['id'], 'Password': other_password}]})
jellyfin(config_path, admin, config)
me = credentials(viewer_name, password)
try:
    assert call('ping', me)['status'] == 'ok'
    assert call('ping', credentials(viewer_name, 'wrong-' + password))['error']['code'] == 40
    # An app password only works for a user StreamFin allows.
    assert call('ping', credentials(other_name, other_password))['error']['code'] == 40
    assert call('ping', {'apiKey': password, 'f': 'json'})['status'] == 'ok'
    assert call('ping', {'u': viewer_name, 'p': 'enc:' + password.encode().hex(), 'f': 'json'})['status'] == 'ok'
    status, headers, body = call('ping', {k: v for k, v in me.items() if k != 'f'}, raw=True)
    assert headers.get_content_type() == 'text/xml' and b'status="ok"' in body
    print('PASS: token, plain, hex and API key auth; wrong passwords and disallowed users are refused', flush=True)

    # Wrong guesses slow an address down but never lock the owner out.
    for _ in range(12):
        assert call('ping', {'u': viewer_name, 'p': 'wrong', 'f': 'json'})['error']['code'] == 40
    started = time.monotonic()
    assert call('ping', credentials(viewer_name, password))['status'] == 'ok' and time.monotonic() - started < 1.5
    assert call('ping', {'u': 'x' * 5000, 'p': 'wrong', 'f': 'json'})['error']['code'] == 40
    # Refused before the form is read, so the format has to be in the address.
    big = urllib.parse.urlencode({'u': 'x' * 300_000, 'p': 'x'}).encode()
    with urllib.request.urlopen(urllib.request.Request(REST + 'ping?f=json', data=big), timeout=30) as res:
        assert json.loads(res.read())['subsonic-response']['error']['code'] == 0
    print('PASS: failed sign-ins never lock out a right password; huge names and forms are refused', flush=True)

    with urllib.request.urlopen(args.url + '/StreamFin', timeout=30) as res:
        assert res.headers.get_content_type() == 'text/html' and b'/StreamFin' in res.read()
    print('PASS: the Subsonic address opened in a browser explains itself', flush=True)

    songs = call('search3', me, query='daft punk one more time', artistCount=0, albumCount=0)['searchResult3']['song']
    song = next(s for s in songs if s['title'] == 'One More Time')
    assert song['suffix'] == 'flac' and song['album'] and song['duration'] > 300 and song['id'].startswith('tr-'), song
    # Amperfy lists only songs with a size.
    assert song['size'] > song['duration'] * 50_000, song
    album = call('getAlbum', me, id=song['albumId'])['album']
    assert album['songCount'] == len(album['song']) and any(s['id'] == song['id'] for s in album['song'])
    assert len(call('getArtist', me, id=song['artistId'])['artist']['album']) > 3
    status, headers, body = call('getCoverArt', me, raw=True, id=song['albumId'])
    assert headers.get_content_type().startswith('image/') and len(body) > 1000
    status, headers, small = call('getCoverArt', me, raw=True, id=song['albumId'], size='64')
    assert headers.get_content_type() == 'image/jpeg' and small[:3] == b'\xff\xd8\xff' and len(small) < len(body)
    print('PASS: search, album, artist and cover art, also at the size an app asks for', flush=True)

    status, headers, body = call('stream', me, raw=True, id=song['id'], headers={'Range': 'bytes=0-65535'})
    assert status == 206 and headers.get_content_type() == 'audio/flac' and body[:4] == b'fLaC' and len(body) == 65536
    upstream = urllib.request.urlopen(urllib.request.Request('https://tracks.monochrome.st/track/' + song['id'].rsplit('-', 1)[1],
                                                             headers={'User-Agent': 'Mozilla/5.0', 'Range': 'bytes=0-65535'}), timeout=60).read()
    assert body == upstream, 'stream did not pass the catalog bytes through unchanged'
    gain = call('getSong', me, id=song['id'])['song']['replayGain']
    assert -30 < gain['trackGain'] < 10 and 0 < gain['trackPeak'] <= 2, gain
    status, headers, body = call('stream', me, raw=True, id=song['id'], format='raw', maxBitRate='128', headers={'Range': 'bytes=0-3'})
    assert headers.get_content_type() == 'audio/flac' and body == b'fLaC', 'format=raw must not be converted'
    status, headers, body = call('stream', me, raw=True, id=song['id'], format='mp3', headers={'Range': 'bytes=0-3'})
    assert headers.get_content_type() == 'audio/flac' and body == b'fLaC', 'MP3 without a bit rate limit gets the lossless original'
    status, headers, body = call('stream', me, raw=True, limit=32768, id=song['id'], format='mp3', maxBitRate='128')
    assert headers.get_content_type() == 'audio/mpeg' and len(body) == 32768
    # Apple's player needs ranges and a length, or iOS apps silently refuse a converted song.
    status, headers, body = call('stream', me, raw=True, id=song['id'], format='mp3', maxBitRate='128', headers={'Range': 'bytes=0-1'})
    total = int(headers['Content-Range'].rsplit('/', 1)[1])
    assert status == 206 and body == b'\xff\xfb' and abs(total - song['duration'] * 128 * 125) < 128 * 125, (status, headers['Content-Range'])
    status, headers, body = call('stream', me, raw=True, limit=16384, id=song['id'], format='mp3', maxBitRate='128', headers={'Range': f'bytes={total // 2}-'})
    assert status == 206 and headers['Content-Range'] == f'bytes {total // 2}-{total - 1}/{total}' and b'\xff\xfb' in body[:2048]
    print('PASS: stream is the catalog FLAC byte for byte and teaches the song its ReplayGain; mp3 is converted on request, with ranges for iOS', flush=True)

    lyrics = call('getLyricsBySongId', me, id=song['id'])['lyricsList']['structuredLyrics'][0]
    assert lyrics['synced'] and lyrics['line'] and 'start' in lyrics['line'][-1]
    print('PASS: synced lyrics', flush=True)

    accented = next(x for x in call('search3', me, query='sigur ros hoppipolla', artistCount=0, albumCount=0)['searchResult3']['song'] if any(ord(c) > 127 for c in x['title']))
    req = urllib.request.Request(REST + 'download.view?' + urllib.parse.urlencode({**me, 'id': accented['id']}), method='HEAD')
    with urllib.request.urlopen(req, timeout=60) as res:
        disposition = res.headers['Content-Disposition']
    assert res.status == 200 and 'filename*=' in disposition and disposition.isascii(), disposition
    print('PASS: downloads of songs with accented titles are named properly', flush=True)

    for key in ('id', 'albumId', 'artistId'):
        assert call('star', me, **{key: song[key]})['status'] == 'ok'
    starred = call('getStarred2', me)['starred2']
    assert starred['song'][0]['id'] == song['id'] and starred['album'] and starred['artist']
    # Stars from an app show up in the Jellyfin Music channel too.
    music = next(c for c in jellyfin('/Channels?UserId=' + viewer['id'], viewer['token'])['Items'] if c['Name'] == 'Music')
    folder = next(f for f in jellyfin('/Items?ParentId=' + music['Id'] + '&UserId=' + viewer['id'], viewer['token'])['Items'] if f['Name'] == 'Starred')
    names = [i['Name'] for i in jellyfin('/Items?ParentId=' + folder['Id'] + '&UserId=' + viewer['id'], viewer['token'])['Items']]
    assert song['title'] in names and song['album'] in names, names
    assert call('scrobble', me, id=song['id'])['status'] == 'ok'
    assert any(a['id'] == song['albumId'] for a in call('getAlbumList2', me, type='recent')['albumList2']['album'])
    created = call('createPlaylist', me, name='Smoke', songId=[song['id'], song['id']])['playlist']
    assert created['songCount'] == 2
    assert call('updatePlaylist', me, playlistId=created['id'], songIndexToRemove=['0'], name='Smoke 2')['status'] == 'ok'
    assert call('getPlaylist', me, id=created['id'])['playlist']['songCount'] == 1
    assert call('deletePlaylist', me, id=created['id'])['status'] == 'ok'
    form = urllib.parse.urlencode({**me, 'name': 'Smoke long', 'songId': [song['id']] * 1500}, doseq=True).encode()
    with urllib.request.urlopen(urllib.request.Request(REST + 'createPlaylist', data=form), timeout=60) as res:
        long = json.loads(res.read())['subsonic-response']['playlist']
    assert long['songCount'] == 1500 and call('deletePlaylist', me, id=long['id'])['status'] == 'ok'
    form = urllib.parse.urlencode({**me, 'name': 'Smoke too long', 'songId': [song['id']] * 2001}, doseq=True).encode()
    with urllib.request.urlopen(urllib.request.Request(REST + 'createPlaylist', data=form), timeout=60) as res:
        assert 'error' in json.loads(res.read())['subsonic-response']
    assert not any(p['name'] == 'Smoke too long' for p in call('getPlaylists', me)['playlists'].get('playlist', []))
    assert call('unstar', me, id=song['id'])['status'] == 'ok'
    print('PASS: stars (also in the channel\'s Starred folder), scrobbles, recent albums and playlists, including a long one sent as a form', flush=True)

    with urllib.request.urlopen(REST + 'getOpenSubsonicExtensions.view?f=json', timeout=30) as res:
        extensions = json.loads(res.read())['subsonic-response']
    assert extensions['status'] == 'ok' and any(e['name'] == 'songLyrics' for e in extensions['openSubsonicExtensions'])
    status, headers, body = call('ping', {**me, 'f': 'jsonp'}, raw=True, callback='app.done')
    assert headers.get_content_type() == 'application/javascript' and body.startswith(b'/**/app.done({') and body.endswith(b');')
    status, headers, body = call('ping', {**me, 'f': 'jsonp'}, raw=True, callback='alert(1)//')
    assert headers.get_content_type() == 'application/json' and body.startswith(b'{')
    assert call('tokenInfo', {'apiKey': password, 'f': 'json'})['tokenInfo']['username'] == viewer_name
    assert call('getUser', me, username=other_name)['error']['code'] == 50
    print('PASS: extensions answer without sign-in, JSONP only with a plain callback name, tokenInfo, getUser only for yourself', flush=True)

    assert call('setRating', me, id=song['albumId'], rating='5')['status'] == 'ok'
    assert call('setRating', me, id=song['id'], rating='4')['status'] == 'ok'
    assert call('getSong', me, id=song['id'])['song']['userRating'] == 4
    highest = call('getAlbumList2', me, type='highest')['albumList2']['album']
    assert highest[0]['id'] == song['albumId'] and highest[0]['userRating'] == 5
    rated = call('getAlbum', me, id=song['albumId'])['album']
    assert rated['originalReleaseDate']['year'] == rated['year'] and rated['song'][0]['year'] == rated['year'] and rated['playCount'] >= 1
    assert call('scrobble', me, id=song['id'], submission='false')['status'] == 'ok'
    assert call('getNowPlaying', me)['nowPlaying']['entry'][0]['id'] == song['id']
    queued = [s['id'] for s in album['song'][:3]]
    assert call('savePlayQueue', me, id=queued, current=queued[1], position='42000')['status'] == 'ok'
    queue = call('getPlayQueue', me)['playQueue']
    assert [e['id'] for e in queue['entry']] == queued and queue['current'] == queued[1] and queue['position'] == 42000
    assert call('createBookmark', me, id=song['id'], position='1000', comment='halfway')['status'] == 'ok'
    assert call('getBookmarks', me)['bookmarks']['bookmark'][0]['entry']['id'] == song['id']
    assert call('deleteBookmark', me, id=song['id'])['status'] == 'ok' and not call('getBookmarks', me)['bookmarks'].get('bookmark')
    changed = call('getIndexes', me)['indexes']['lastModified']
    assert 'index' not in call('getIndexes', me, ifModifiedSince=str(changed))['indexes']
    assert call('getIndexes', me)['indexes']['lastModified'] == changed
    print('PASS: ratings, play counts, release dates, now playing, play queue, bookmarks and a stable index date', flush=True)

    # The starred artist's other albums join the library once the server has their page.
    grown = []
    for _ in range(12):
        grown = call('getAlbumList2', me, type='alphabeticalByName', size='500')['albumList2'].get('album', [])
        if len(grown) > 3:
            break
        time.sleep(5)
    assert len(grown) > 3, f'the library did not grow from the starred artist ({len(grown)} albums)'
    print(f'PASS: the library grows from a starred artist ({len(grown)} albums)', flush=True)

    # The other user has their own library, even with access.
    config['AllowedUserIds'] = [viewer['id'], other['id']]
    jellyfin(config_path, admin, config)
    them = credentials(other_name, other_password)
    assert call('ping', them)['status'] == 'ok' and not call('getStarred2', them)['starred2'].get('album')
    assert 'playQueue' not in call('getPlayQueue', them) and not call('getNowPlaying', them)['nowPlaying'].get('entry')
    assert call('getSong', them, id=song['id'])['song'].get('userRating') is None
    config['SubsonicEnabled'] = False
    jellyfin(config_path, admin, config)
    time.sleep(1)
    assert call('ping', me)['error']['code'] == 50
    print('PASS: libraries are per user, and turning the API off refuses every request', flush=True)
finally:
    jellyfin(config_path, admin, {**original, 'CacheRevision': original.get('CacheRevision', 0) + 1})
