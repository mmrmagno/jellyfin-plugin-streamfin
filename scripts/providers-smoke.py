#!/usr/bin/env python3
"""Provider routing and saved-progress checks; disposable instance only."""
import argparse
import json
import pathlib
import time
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
args = parser.parse_args()
state = json.loads(pathlib.Path('.test-state/session.secret').read_text())
marc = state['users']['marc']['token']
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'


def request(path, token=marc, data=None):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'MediaBrowser Token="' + token + '"'
    req = urllib.request.Request(args.url + path, headers=headers, data=json.dumps(data).encode() if data is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=120) as response:
            raw = response.read()
            return response.status, json.loads(raw) if raw else None
    except urllib.error.HTTPError as error:
        return error.code, None


def ok(path, token=marc, data=None):
    status, body = request(path, token, data)
    assert status < 400, (path, status)
    return body


def children(parent):
    return ok('/Items?ParentId=' + parent + '&Fields=ProviderIds')['Items']


def search(term):
    return ok('/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm=' + urllib.parse.quote(term))['Items']


assert ok('/System/Info/Public')['ServerName'] == 'AniFin isolated test'
config = ok(config_path, state['admin'])
try:
    enabled = dict(config, AnimeGgEnabled=True, AnimeHeavenEnabled=True, CacheRevision=int(time.time()))
    ok(config_path, state['admin'], enabled)
    series = next(x for x in search('Cowboy Bebop') if x['Name'] == 'Cowboy Bebop')
    seasons = children(series['Id'])
    print('Provider choices:', ', '.join(s['Name'] for s in seasons), flush=True)
    gg = next(x for x in seasons if x['Name'] == 'Episodes · AnimeGG')
    heaven = next(x for x in seasons if x['Name'] == 'Episodes · AnimeHeaven')
    gg_episodes = children(gg['Id'])
    heaven_episodes = children(heaven['Id'])
    assert len(heaven_episodes) == 26
    saved_path = pathlib.Path('.test-state/clear.secret')
    if saved_path.exists():
        saved = json.loads(saved_path.read_text())
        assert set(saved['episodes']).issubset({x['Id'] for x in gg_episodes}), 'Legacy AnimeGG episode IDs changed'
    played = gg_episodes[0]['Id']
    before = ok('/UserItems/' + played + '/UserData')
    episode = heaven_episodes[0]['Id']
    ok('/UserItems/' + episode + '/UserData', data={'Played': False, 'PlaybackPositionTicks': 1250000000, 'PlayCount': 1})
    before_heaven = ok('/UserItems/' + episode + '/UserData')
    info = ok('/Items/' + episode + '/PlaybackInfo')
    media = next(s for s in info['MediaSources'] if '/AniFin/' in s.get('Path', ''))
    assert media['Name'].startswith('AnimeHeaven') and media['MediaStreams'] and media['RunTimeTicks'] > 0
    print('PASS: AnimeHeaven episode resolves and FFprobe reads its media streams', flush=True)
    for token in (None, state['users']['other']['token']):
        assert request('/Items/' + episode + '/PlaybackInfo', token)[0] in (401, 403, 404)
    print('PASS: new-provider playback remains restricted', flush=True)
    for flag, hidden, visible in [('AnimeHeavenEnabled', episode, played), ('AnimeGgEnabled', played, episode)]:
        ok(config_path, state['admin'], dict(enabled, **{flag: False}))
        listed = children(series['Id'])
        assert all(x['Id'] != (heaven['Id'] if flag == 'AnimeHeavenEnabled' else gg['Id']) for x in listed)
        assert request('/Items/' + hidden + '/PlaybackInfo')[0] == 404
        assert request('/Items/' + visible)[0] == 200
        ok(config_path, state['admin'], enabled)
        restored = children(series['Id'])
        assert {gg['Id'], heaven['Id']}.issubset({x['Id'] for x in restored})
        children(gg['Id']); children(heaven['Id'])
        for item, previous in [(played, before), (episode, before_heaven)]:
            after = ok('/UserItems/' + item + '/UserData')
            for key in ('Played', 'PlaybackPositionTicks', 'PlayCount', 'IsFavorite'):
                assert after[key] == previous[key], (flag, key)
    print('PASS: independent provider switches deny cached playback and retain episode IDs/progress', flush=True)
    ok(config_path, state['admin'], dict(enabled, AnimeGgEnabled=False))
    assert any('Frieren' in x['Name'] for x in search('Sousou no Frieren'))
    print('PASS: online catalog search works with AnimeHeaven alone', flush=True)
    output = pathlib.Path('.test-state/providers.secret')
    output.write_text(json.dumps({'series': series['Id'], 'season': heaven['Id'], 'episode': episode}))
    output.chmod(0o600)
finally:
    ok(config_path, state['admin'], config)
