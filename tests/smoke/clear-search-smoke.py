#!/usr/bin/env python3
import argparse
import json
import pathlib
import urllib.error
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--verify-after-restart', action='store_true')
args = parser.parse_args()
state = json.loads(pathlib.Path('.test-state/session.secret').read_text())
viewer = state['users']['viewer']['token']
clear_path = '/StreamFin/SearchResults/Clear'
saved_path = pathlib.Path('.test-state/clear.secret')

def request(path, token=viewer, data=None):
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

def ok(path, token=viewer, data=None):
    status, body = request(path, token, data)
    assert status < 400, (path, status)
    return body

def children(parent):
    return ok('/Items?ParentId=' + parent)['Items']

def search():
    return ok('/Items?Recursive=true&IncludeItemTypes=Series&SearchTerm=Cowboy%20Bebop')['Items']

def user_data(item):
    data = ok('/UserItems/' + item + '/UserData')
    return {key: data[key] for key in ('Played', 'PlaybackPositionTicks', 'PlayCount', 'IsFavorite')}

assert ok('/System/Info/Public')['ServerName'] == 'StreamFin isolated test'
if not args.verify_after_restart:
    series = next(x for x in search() if x['Name'] == 'Cowboy Bebop')
    ok('/UserItems/' + series['Id'] + '/UserData', data={'IsFavorite': True})
    episodes = children(children(series['Id'])[0]['Id'])
    watched, resumed = episodes[0]['Id'], episodes[1]['Id']
    ok('/UserItems/' + watched + '/UserData', data={'Played': True, 'PlayCount': 1})
    ok('/UserItems/' + resumed + '/UserData', data={'Played': False, 'PlaybackPositionTicks': 1250000000, 'PlayCount': 1})
    channel = next(x for x in ok('/Channels')['Items'] if x['Name'] == 'Anime')
    folder = next(x for x in children(channel['Id']) if x['Name'] == 'Search results')
    saved = {'folder': folder['Id'], 'series': series['Id'], 'episodes': {item: user_data(item) for item in (watched, resumed)}}
    for token in (None, state['users']['other']['token'], viewer):
        assert request(clear_path, token, {})[0] in (401, 403)
    print('PASS: clearing requires an administrator', flush=True)
    assert ok(clear_path, state['admin'], {})['ClearedCount'] > 0
    assert ok(clear_path, state['admin'], {})['ClearedCount'] == 0
    config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'
    config = ok(config_path, state['admin'])
    config['CacheRevision'] += 1
    ok(config_path, state['admin'], config)
    saved_path.write_text(json.dumps(saved))
    saved_path.chmod(0o600)
else:
    saved = json.loads(saved_path.read_text())

assert children(saved['folder']) == [], 'Cleared results reappeared'
favorites = ok('/Items?Recursive=true&IncludeItemTypes=Series&Filters=IsFavorite')['Items']
assert any(item['Id'] == saved['series'] for item in favorites), 'Clearing hid a favorite'
for item, before in saved['episodes'].items():
    assert user_data(item) == before, 'Watched status or resume position changed'
print('PASS: results empty; watched status and resume position preserved', flush=True)
if args.verify_after_restart:
    assert next(x for x in search() if x['Name'] == 'Cowboy Bebop')['Id'] == saved['series']
    assert children(saved['folder'])
    for item, before in saved['episodes'].items():
        assert user_data(item) == before
    print('PASS: clearing survives restart; searching again restores titles with their original progress', flush=True)
