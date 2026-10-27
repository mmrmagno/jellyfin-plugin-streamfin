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
    album = call('getAlbum', me, id=song['albumId'])['album']
    assert album['songCount'] == len(album['song']) and any(s['id'] == song['id'] for s in album['song'])
    assert len(call('getArtist', me, id=song['artistId'])['artist']['album']) > 3
    status, headers, body = call('getCoverArt', me, raw=True, id=song['albumId'])
    assert headers.get_content_type().startswith('image/') and len(body) > 1000
    print('PASS: search, album, artist and cover art', flush=True)

    status, headers, body = call('stream', me, raw=True, id=song['id'], headers={'Range': 'bytes=0-65535'})
    assert status == 206 and headers.get_content_type() == 'audio/flac' and body[:4] == b'fLaC' and len(body) == 65536
    upstream = urllib.request.urlopen(urllib.request.Request('https://tracks.monochrome.st/track/' + song['id'].rsplit('-', 1)[1],
                                                             headers={'User-Agent': 'Mozilla/5.0', 'Range': 'bytes=0-65535'}), timeout=60).read()
    assert body == upstream, 'stream did not pass the catalog bytes through unchanged'
    gain = call('getSong', me, id=song['id'])['song']['replayGain']
    assert -30 < gain['trackGain'] < 10 and 0 < gain['trackPeak'] <= 2, gain
    status, headers, body = call('stream', me, raw=True, id=song['id'], format='raw', maxBitRate='128', headers={'Range': 'bytes=0-3'})
    assert headers.get_content_type() == 'audio/flac' and body == b'fLaC', 'format=raw must not be converted'
    status, headers, body = call('stream', me, raw=True, limit=32768, id=song['id'], format='mp3', maxBitRate='128')
    assert headers.get_content_type() == 'audio/mpeg' and len(body) == 32768
    print('PASS: stream is the catalog FLAC byte for byte and teaches the song its ReplayGain; mp3 is converted on request', flush=True)

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
    print('PASS: stars, scrobbles, recent albums and playlists, including a long one sent as a form', flush=True)

    # The other user has their own library, even with access.
    config['AllowedUserIds'] = [viewer['id'], other['id']]
    jellyfin(config_path, admin, config)
    them = credentials(other_name, other_password)
    assert call('ping', them)['status'] == 'ok' and not call('getStarred2', them)['starred2'].get('album')
    config['SubsonicEnabled'] = False
    jellyfin(config_path, admin, config)
    time.sleep(1)
    assert call('ping', me)['error']['code'] == 50
    print('PASS: libraries are per user, and turning the API off refuses every request', flush=True)
finally:
    jellyfin(config_path, admin, {**original, 'CacheRevision': original.get('CacheRevision', 0) + 1})
