namespace StreamFin;

// The playlist import page at <server>/StreamFin/Import. It uses the sign-in the Jellyfin web app keeps in this browser,
// so a user opens it after signing in to Jellyfin; nothing is asked for twice. NONCE is replaced per request.
public static class ImportPage
{
    public const string Html = """
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Import a playlist · StreamFin</title><link rel="icon" href="data:,">
        <style nonce="NONCE">
        :root{--bg:#f4f4f6;--card:#fff;--text:#1d1d1f;--muted:#5f6368;--line:#dcdce0;--accent:#0089b8;--accent-text:#fff;--bad:#c5221f;--good:#1e7d32}
        @media (prefers-color-scheme:dark){:root{--bg:#101010;--card:#1c1c1c;--text:#eee;--muted:#a3a3a3;--line:#333;--accent:#00a4dc;--accent-text:#fff;--bad:#ff8a80;--good:#81c995}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:16px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:46rem;margin:0 auto;padding:1.5rem 1rem 4rem}
        h1{font-size:1.6rem;margin:.5rem 0 .25rem}h2{font-size:1.1rem;margin:0 0 .75rem}
        p{margin:.25rem 0 .75rem}.muted{color:var(--muted)}a{color:var(--accent)}
        .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:1.1rem 1.1rem 1rem;margin:1rem 0}
        textarea,input[type=text]{width:100%;font:inherit;color:inherit;background:transparent;border:1px solid var(--line);border-radius:8px;padding:.6rem .7rem}
        textarea{min-height:9rem;resize:vertical;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:.9rem}
        label{display:block;font-weight:600;margin:.75rem 0 .3rem}
        button{font:inherit;font-weight:600;border:0;border-radius:8px;padding:.6rem 1.1rem;background:var(--accent);color:var(--accent-text);cursor:pointer}
        button.secondary{background:transparent;color:var(--accent);border:1px solid var(--line)}
        button:disabled{opacity:.5;cursor:default}
        .row{display:flex;gap:.6rem;align-items:center;flex-wrap:wrap;margin-top:.9rem}
        .bar{height:8px;background:var(--line);border-radius:4px;overflow:hidden;margin:.6rem 0}.bar>div{height:100%;width:0;background:var(--accent);transition:width .4s}
        .bad{color:var(--bad)}.good{color:var(--good)}
        ul.lists{list-style:none;padding:0;margin:0}ul.lists li{display:flex;gap:.75rem;align-items:center;justify-content:space-between;padding:.6rem 0;border-top:1px solid var(--line)}
        ul.lists li:first-child{border-top:0}.name{font-weight:600}.small{font-size:.875rem}
        details{margin-top:.5rem}summary{cursor:pointer;color:var(--accent)}
        dl{margin:0}dt{font-weight:600;margin-top:.75rem}dd{margin:.15rem 0 0}code{background:var(--line);border-radius:4px;padding:0 .3rem}
        pre{white-space:pre-wrap;max-height:14rem;overflow:auto;background:var(--bg);border-radius:8px;padding:.6rem;font-size:.85rem}
        [hidden]{display:none!important}
        </style></head><body><main>
        <h1>Import a playlist</h1>
        <p class="muted">Songs are matched to StreamFin's music and the playlist appears under Music &gt; Playlists, and in Subsonic apps.</p>
        <div id="signin" class="card" hidden><h2>Sign in first</h2><p>Open <a id="web" href="../web/">Jellyfin</a> in this browser, sign in, then come back to this page.</p></div>
        <div id="off" class="card" hidden><p id="offText"></p></div>
        <div id="app" hidden>
          <section class="card">
            <h2>Paste</h2>
            <label for="text">A playlist link, songs copied from Spotify, or a song list</label>
            <textarea id="text" spellcheck="false" placeholder="https://open.spotify.com/playlist/...&#10;or one song per line: Artist - Title"></textarea>
            <label for="name">Playlist name (optional)</label>
            <input type="text" id="name" maxlength="200" autocomplete="off">
            <div class="row"><button id="go" type="button">Import</button><span id="kind" class="muted small"></span></div>
          </section>
          <section class="card">
            <h2>Spotify export file</h2>
            <p class="muted small">From Spotify's "Download your data": <code>Playlist1.json</code> holds your playlists, <code>YourLibrary.json</code> your Liked Songs.</p>
            <input type="file" id="file" accept=".json,application/json">
            <ul id="exportLists" class="lists"></ul>
          </section>
          <section id="progress" class="card" hidden>
            <h2 id="progressName">Importing</h2>
            <div class="bar"><div id="progressBar"></div></div>
            <p id="progressText" role="status"></p>
            <p id="progressNote" class="muted small"></p>
            <details id="missedBox" hidden><summary id="missedSummary"></summary><p class="muted small">These could not be found. Fix a name and paste it again to add it.</p><pre id="missed"></pre></details>
          </section>
          <section class="card">
            <h2>Your playlists</h2>
            <ul id="lists" class="lists"><li class="muted">Loading…</li></ul>
          </section>
          <section class="card">
            <h2>What you can paste</h2>
            <dl class="small">
              <dt>A Spotify playlist or album link</dt><dd>Any public one, including playlists you follow and ones Spotify made. Spotify shares the first 100 songs of a playlist this way.</dd>
              <dt>Songs copied in the Spotify app</dt><dd>Open any playlist (private ones and Liked Songs too), press Ctrl+A then Ctrl+C (Cmd on a Mac), and paste. No limit.</dd>
              <dt>A YouTube or YouTube Music playlist link</dt><dd id="ytHelp">Needs YouTube switched on in the StreamFin settings.</dd>
              <dt>A Deezer playlist link</dt><dd>Public playlists. Songs are matched exactly.</dd>
              <dt>A song list or CSV</dt><dd>"Artist - Title", one per line, or a CSV from Exportify, TuneMyMusic or Soundiiz.</dd>
            </dl>
            <p class="muted small">A playlist imported from a link can be imported again later; that updates it in place.</p>
          </section>
        </div>
        </main>
        <script nonce="NONCE">
        (function () {
          const $ = id => document.getElementById(id);
          function token() {
            try {
              const saved = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
              const servers = (saved.Servers || []).filter(s => s.AccessToken);
              const here = servers.find(s => [s.ManualAddress, s.LocalAddress, s.RemoteAddress].some(a => { try { return a && new URL(a).origin === location.origin; } catch { return false; } }));
              // Another server's sign-in is never sent here; a single saved server is this one.
              return (here || (servers.length === 1 ? servers[0] : {})).AccessToken || null;
            } catch { return null; }
          }
          const auth = token();
          // Addresses are built from this page's own path, so a trailing slash or a Jellyfin base path works.
          const base = location.pathname.replace(/\/+$/, '');
          document.getElementById('web').href = base.replace(/\/StreamFin\/Import$/i, '') + '/web/';
          async function call(path, method, body) {
            const response = await fetch(base + '/' + path, { method: method || 'GET', headers: Object.assign({ 'Authorization': 'MediaBrowser Token="' + auth + '"' },
              body ? { 'Content-Type': 'application/json' } : {}), body: body ? JSON.stringify(body) : undefined });
            const data = await response.json().catch(() => ({}));
            if (response.status === 401 || response.status === 403) throw new Error(response.status === 403 ? 'StreamFin is not turned on for your account.' : 'Your Jellyfin sign-in has expired. Sign in again.');
            if (!response.ok) throw new Error(data.Message || 'Something went wrong. Please try again.');
            return data;
          }
          function kindOf(text) {
            const t = text.trim(); if (!t) return '';
            const lines = t.split('\n').filter(l => l.trim());
            const tracks = lines.filter(l => /spotify\.com\/(intl-[a-z-]+\/)?track\/|spotify:track:/i.test(l)).length;
            if (tracks && tracks >= lines.length / 2) return tracks + ' Spotify songs';
            if (lines.length === 1 && /spotify\.com\/(intl-[a-z-]+\/)?album\/|spotify:album:/i.test(t)) return 'Spotify album';
            if (lines.length === 1 && /spotify\.com\/(intl-[a-z-]+\/)?playlist\/|spotify:playlist:/i.test(t)) return 'Spotify playlist';
            if (lines.length === 1 && /youtu(\.be|be\.com)\/.*[?&]list=/i.test(t)) return 'YouTube playlist';
            if (lines.length === 1 && /deezer\.com\/([a-z]{2}\/)?playlist\//i.test(t)) return 'Deezer playlist';
            return lines.length + (lines.length === 1 ? ' line' : ' lines');
          }
          let polling = null;
          function follow(id) {
            clearInterval(polling);
            $('progress').hidden = false; $('missedBox').hidden = true; $('progressNote').textContent = '';
            $('progressText').className = ''; $('progressText').textContent = 'Reading the playlist…'; $('progressBar').style.width = '0';
            polling = setInterval(async () => {
              let job;
              try { job = await call('Jobs/' + id); } catch (error) { clearInterval(polling); $('progressText').textContent = error.message; return; }
              $('progressName').textContent = job.Name || 'Importing';
              const share = job.Total ? Math.round(100 * job.Done / job.Total) : 0;
              $('progressBar').style.width = share + '%';
              if (job.State === 'reading') $('progressText').textContent = 'Reading the playlist…';
              else if (job.State === 'matching') $('progressText').textContent = 'Matching songs: ' + job.Done + ' of ' + job.Total + ' checked, ' + job.Found + ' found.';
              else if (job.State === 'failed') { $('progressText').className = 'bad'; $('progressText').textContent = job.Message; }
              else { $('progressBar').style.width = '100%'; $('progressText').className = 'good'; $('progressText').textContent = 'Done: ' + job.Found + ' of ' + job.Total + ' songs found. The playlist is under Music > Playlists.'; $('progressNote').textContent = job.Message || ''; }
              if (job.NotFound && job.NotFound.length) { $('missedBox').hidden = false; $('missedSummary').textContent = job.NotFound.length + ' songs not found'; $('missed').textContent = job.NotFound.join('\n'); }
              if (job.State === 'done' || job.State === 'failed') { clearInterval(polling); $('go').disabled = false; render(); }
            }, 1500);
          }
          async function start(action) {
            $('go').disabled = true;
            try { follow((await action()).Id); }
            catch (error) { $('go').disabled = false; $('progress').hidden = false; $('progressName').textContent = 'Not imported'; $('progressBar').style.width = '0'; $('progressText').className = 'bad'; $('progressText').textContent = error.message; }
          }
          async function render() {
            const data = await call('Playlists');
            if (!data.Enabled) { $('app').hidden = true; $('off').hidden = false; $('offText').textContent = 'Playlist import is switched off on this server. An administrator can turn it on in the StreamFin settings.'; return; }
            $('ytHelp').textContent = data.YouTube ? 'Public playlists, matched by title, artist and length.' : 'Needs YouTube switched on in the StreamFin settings.';
            const list = $('lists'); list.replaceChildren();
            if (!data.Playlists.length) { const li = document.createElement('li'); li.className = 'muted'; li.textContent = 'No playlists yet.'; list.append(li); }
            data.Playlists.forEach(p => {
              const li = document.createElement('li'); const text = document.createElement('div');
              const name = document.createElement('div'); name.className = 'name'; name.textContent = p.Name;
              const about = document.createElement('div'); about.className = 'muted small'; about.textContent = p.Songs + ' songs' + (p.Comment ? ' · ' + p.Comment : '');
              text.append(name, about); li.append(text);
              if (p.Reimport) {
                const again = document.createElement('button'); again.type = 'button'; again.className = 'secondary'; again.textContent = 'Import again';
                again.addEventListener('click', () => start(() => call('Playlists/' + p.Id + '/Reimport', 'POST')));
                li.append(again);
              }
              list.append(li);
            });
          }
          $('text').addEventListener('input', () => { $('kind').textContent = kindOf($('text').value); });
          $('go').addEventListener('click', () => {
            if ($('text').value.length > 1000000) { $('progress').hidden = false; $('progressName').textContent = 'Not imported'; $('progressText').className = 'bad'; $('progressText').textContent = 'That is too much text. Import at most 2000 songs at a time.'; return; }
            start(() => call('Start', 'POST', { Text: $('text').value, Name: $('name').value }));
          });
          $('file').addEventListener('change', async () => {
            const file = $('file').files[0]; const box = $('exportLists'); box.replaceChildren();
            if (!file) return;
            if (file.size > 6 * 1024 * 1024) { const li = document.createElement('li'); li.className = 'bad'; li.textContent = 'This file is larger than 6 MB.'; box.append(li); return; }
            try {
              const lists = await call('Export', 'POST', { Text: await file.text() });
              lists.forEach(l => {
                const li = document.createElement('li'); const label = document.createElement('div');
                label.textContent = l.Name + ' (' + l.Songs + ' songs)';
                const go = document.createElement('button'); go.type = 'button'; go.className = 'secondary'; go.textContent = 'Import';
                go.addEventListener('click', () => start(() => call('Export/' + l.Index, 'POST')));
                li.append(label, go); box.append(li);
              });
            } catch (error) { const li = document.createElement('li'); li.className = 'bad'; li.textContent = error.message; box.append(li); }
          });
          if (!auth) { $('signin').hidden = false; return; }
          $('app').hidden = false;
          render().catch(error => { $('app').hidden = true; $('off').hidden = false; $('offText').textContent = error.message; });
        })();
        </script></body></html>
        """;
}
