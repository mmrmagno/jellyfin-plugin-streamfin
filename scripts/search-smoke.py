#!/usr/bin/env python3
"""Online search and content-policy checks. Uses disposable smoke.py state only."""
import argparse
import json
import pathlib
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--url', default='http://127.0.0.1:18096')
parser.add_argument('--state', default='.test-state/session.secret')
args = parser.parse_args()
state = json.loads(pathlib.Path(args.state).read_text())
config_path = '/Plugins/a74fdb2e-938f-483b-b2ef-f527e14ff724/Configuration'

def request(path, token, data=None):
    req = urllib.request.Request(args.url + path, headers={'Authorization': 'MediaBrowser Token="' + token + '"', 'Content-Type': 'application/json'}, data=json.dumps(data).encode() if data is not None else None)
    try:
        with urllib.request.urlopen(req, timeout=120) as res:
            raw = res.read()
            return res.status, json.loads(raw) if raw and res.headers.get_content_type() == 'application/json' else raw
    except urllib.error.HTTPError as error:
        return error.code, None

def ok(path, token, data=None):
    status, body = request(path, token, data)
    assert status < 400, (path, status)
    return body

marc = state['users']['marc']['token']
other = state['users']['other']['token']
assert ok('/System/Info/Public', marc)['ServerName'] == 'AniFin isolated test'
config = ok(config_path, state['admin'])

def search(term, token=marc):
    return ok('/Items?Recursive=true&IncludeItemTypes=Series&Fields=Tags,ProviderIds&SearchTerm=' + urllib.parse.quote(term), token)['Items']

try:
    ok(config_path, state['admin'], dict(config, HideAdultContent=True, HideEcchiContent=True))
    results = search('Cowboy Bebop')
    assert any(x['Name'] == 'Cowboy Bebop' and 'AniFin' in x.get('Tags', []) for x in results), results
    print('PASS: online search discovers anime outside the browsed catalog', flush=True)
    aliases = search('Sousou no Frieren')
    assert any('Frieren' in x['Name'] for x in aliases), aliases
    print('PASS: Japanese title query finds English catalog title', flush=True)
    assert not any('AniFin' in x.get('Tags', []) for x in search('Cowboy Bebop', other))
    hints = ok('/Search/Hints?SearchTerm=Cowboy%20Bebop', other)
    assert not any(x.get('Name') == 'Cowboy Bebop' for x in hints['SearchHints'])
    print('PASS: both search endpoints hide AniFin from other users', flush=True)
    ok(config_path, state['admin'], dict(config, HideAdultContent=True, HideEcchiContent=False))
    ecchi = next(x for x in search('High School DxD') if x['Name'] == 'High School DxD')
    ok(config_path, state['admin'], dict(config, HideAdultContent=True, HideEcchiContent=True))
    assert request('/Items/' + ecchi['Id'], marc)[0] == 404
    assert request('/Items?ParentId=' + ecchi['Id'], marc)[0] == 404
    assert all(x['Id'] != ecchi['Id'] for x in search('High School DxD'))
    print('PASS: enabling Ecchi filter hides cached titles and denies direct access', flush=True)
finally:
    ok(config_path, state['admin'], config)
