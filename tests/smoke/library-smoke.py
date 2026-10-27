#!/usr/bin/env python3
# Radio and save to library, on an isolated server prepared by smoke.py: a Jellyfin favourite on a channel album
# becomes a star, radio mixes similar artists in the channel and over Subsonic, and a starred single is saved as
# FLAC into a real music library. The save downloads one whole song from the catalog, so run this sparingly.
# The server needs a writable folder at --music (a volume, for example `-v <dir>:/music`).
import argparse
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
parser.add_argument('--music', default='/music', help='a folder inside the server that Jellyfin can write to')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'
REST = args.url + '/StreamFin/rest/'


def jellyfin(path, token, data=None, method=None):
    req = urllib.request.Request(args.url + path, data=json.dumps(data).encode() if data is not None else None, method=method,
                                 headers={'Authorization': 'MediaBrowser Client="StreamFin Tests", Device="Integration", DeviceId="streamfin-library", Version="0.1.0", Token="' + token + '"',
                                          'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=120) as res:
        body = res.read()
        return json.loads(body) if body else None


def call(method, **params):
    # OpenSubsonic API key: the app password itself, no MD5 token (subsonic-smoke.py covers token sign-in).
    auth = {'apiKey': password, 'v': '1.16.1', 'c': 'smoke', 'f': 'json'}
    with urllib.request.urlopen(REST + method + '.view?' + urllib.parse.urlencode({**auth, **params}, doseq=True), timeout=120) as res:
        return json.loads(res.read())['subsonic-response']


def wait(what, check, seconds):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if (found := check()):
            return found
        time.sleep(2)
    raise AssertionError('timed out waiting for ' + what)


def items(parent, token, user):
    return jellyfin('/Items?ParentId=' + parent + '&UserId=' + user, token)['Items']


admin = state['admin']
viewer = state['users']['viewer']
password = secrets.token_hex(12)
config = jellyfin(config_path, admin)
original = dict(config)
config.update({'Enabled': True, 'MusicEnabled': True, 'MusicApiUrl': '', 'AllowedUserIds': [viewer['id']], 'SubsonicEnabled': True,
               'SubsonicPasswords': [{'UserId': viewer['id'], 'Password': password}], 'MusicLibraryFolder': '', 'CacheRevision': config.get('CacheRevision', 0) + 1})
jellyfin(config_path, admin, config)
try:
    base = '/Items?userId=' + viewer['id'] + '&recursive=true&limit=20&searchTerm=daft%20punk%20discovery'
    album = next(a for a in jellyfin(base + '&includeItemTypes=MusicAlbum', viewer['token'])['Items'] if a['Name'] == 'Discovery')
    jellyfin('/UserFavoriteItems/' + album['Id'] + '?userId=' + viewer['id'], viewer['token'], method='POST')
    wait('the favourite to become a star', lambda: any(a['name'] == 'Discovery' for a in call('getStarred2')['starred2'].get('album', [])), 30)
    jellyfin('/UserFavoriteItems/' + album['Id'] + '?userId=' + viewer['id'], viewer['token'], method='DELETE')
    wait('the star to go', lambda: not any(a['name'] == 'Discovery' for a in call('getStarred2')['starred2'].get('album', [])), 30)
    print('PASS: a favourite on a channel album stars it for music apps, and removing it unstars', flush=True)

    song = next(s for s in call('search3', query='daft punk one more time', artistCount=0, albumCount=0)['searchResult3']['song'] if s['title'] == 'One More Time')
    mix = call('getSimilarSongs2', id=song['artistId'], count=30)['similarSongs2']['song']
    assert len(mix) >= 10 and len({s['artistId'] for s in mix}) >= 3, [(s['artist'], s['title']) for s in mix]
    similar = call('getArtistInfo2', id=song['artistId'])['artistInfo2'].get('similarArtist', [])
    assert similar and all(a['id'].startswith('ar-') and a['id'] != song['artistId'] for a in similar), similar
    print(f'PASS: Subsonic radio mixes {len({s["artistId"] for s in mix})} artists; artist info lists {len(similar)} similar artists', flush=True)

    # Jellyfin also makes a plain artist of that name, outside the channel and empty; the channel's own is the one.
    artist = next(a for a in jellyfin('/Items?userId=' + viewer['id'] + '&recursive=true&limit=20&searchTerm=daft%20punk&includeItemTypes=MusicArtist',
                                      viewer['token'])['Items'] if a['Name'] == 'Daft Punk' and a.get('ChannelId') == album.get('ChannelId'))
    radio = next(f for f in items(artist['Id'], viewer['token'], viewer['id']) if f['Name'] == 'Radio')
    tracks = items(radio['Id'], viewer['token'], viewer['id'])
    assert len(tracks) >= 10 and len({t['AlbumArtist'] for t in tracks if t.get('AlbumArtist')}) >= 3, [(t.get('AlbumArtist'), t['Name']) for t in tracks]
    print('PASS: artist pages in the channel have a Radio folder with similar artists mixed in', flush=True)

    folder = args.music.rstrip('/') + '/StreamFin'
    library = next((v for v in jellyfin('/Library/VirtualFolders', admin) if args.music in v['Locations']), None)
    if library is None:
        jellyfin('/Library/VirtualFolders?' + urllib.parse.urlencode({'name': 'StreamFin smoke', 'collectionType': 'music', 'paths': args.music, 'refreshLibrary': 'false'}),
                 admin, {'LibraryOptions': {}}, method='POST')
    config['MusicLibraryFolder'] = folder
    jellyfin(config_path, admin, config)
    try:
        jellyfin('/StreamFin/MusicSaves', viewer['token'])
        raise AssertionError('a non-admin read the save queue')
    except urllib.error.HTTPError as error:
        assert error.code in (401, 403), error.code
    albums = call('getArtist', id=song['artistId'])['artist']['album']
    single = next((a for a in albums if a.get('songCount') == 1), None) or next(a for a in albums if len(call('getAlbum', id=a['id'])['album']['song']) == 1)
    assert call('star', albumId=single['id'])['status'] == 'ok'

    def status():
        result = jellyfin('/StreamFin/MusicSaves', admin)
        return next((s for s in result['Saves'] if s['Album'] == single['name']), None), result

    save, result = status()
    assert save and result['Folder'] == folder and result['Libraries'], result
    save = wait('the single to be saved', lambda: (s := status()[0]) and s['State'] in ('saved', 'failed') and s, 300)
    assert save['State'] == 'saved' and save['Saved'] == save['Total'] == 1, save
    saved = wait('Jellyfin to list the saved song', lambda: [i for i in jellyfin('/Items?recursive=true&includeItemTypes=Audio&fields=Path,MediaSources&userId=' + viewer['id'], admin)['Items']
                                                           if i.get('Path', '').startswith(folder + '/')], 180)
    path = saved[0]['Path']
    assert path.endswith('.flac') and '/Daft Punk/' in path and path.rsplit('/', 1)[1][:3] == '01 ', path
    print(f'PASS: a starred single is saved as {path.removeprefix(folder + "/")} and Jellyfin picks it up', flush=True)

    assert call('unstar', albumId=single['id'])['status'] == 'ok' and status()[0] is None
    started = time.monotonic()
    assert call('star', albumId=single['id'])['status'] == 'ok'
    wait('the second save', lambda: (s := status()[0]) and s['State'] == 'saved', 60)
    assert time.monotonic() - started < 50, 'a second save downloaded again instead of keeping the file'
    assert call('unstar', albumId=single['id'])['status'] == 'ok'
    print('PASS: unstarring drops the save and keeps the files; starring again keeps the existing file', flush=True)
finally:
    jellyfin(config_path, admin, {**original, 'CacheRevision': config.get('CacheRevision', 0) + 1})
print('Library smoke checks passed.')
