# StreamFin

Online anime, movies and TV for Jellyfin, played with Jellyfin's own player. StreamFin adds two channels:

- **Anime**: browse **Popular**, **This Season**, or **A–Z**, choose a title, then a source, then an episode.
- **Movies & TV**: browse **Popular movies**, **Popular TV**, or **Trending this week**. Movies play directly; shows open into seasons and episodes.

Jellyfin's **magnifying glass** searches both online catalogs, including titles you have not browsed yet. Discovered titles also appear under each channel's **Search results**.

StreamFin was called AniFin up to 0.4. Upgrading keeps the plugin ID, settings, episode IDs, watch progress and favourites (see Install).

StreamFin uses AniList and TMDB for catalog metadata and contacts the streaming sites directly. It does not download into your media library or use torrents/debrid. Jellyfin can temporarily buffer or transcode during playback.

## Compatibility

This build targets Jellyfin **12.3.0**, specifically `jellyfin/jellyfin@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5`, the image mediastack runs. It is compiled against that image's API assemblies with .NET 10. Other server versions need a matching build and testing.

## Anime sources

Open a title and choose one of:

| Source | How it finds the show | Subtitles | Quality |
|---|---|---|---|
| **HiAnime** | MyAnimeList ID from AniList, no title guessing | Separate English track, selectable in the player | up to 1080p |
| **AnimeGG** | Title search | Burned into the video | highest offered |
| **AnimeHeaven** | Title search | Burned into the video | source quality |

Only Japanese audio with English subtitles is offered; dubbed audio is never substituted. HiAnime's episode list follows AniList's aired-episode count, so a brand new episode appears once AniList lists it. If one source fails or lacks an episode, return to the title and choose another. Switching is manual: StreamFin does not swap video files partway through playback. Each source has its own switch under **Dashboard → Plugins → StreamFin**. Disabling a source hides its cached items and denies playback while keeping saved progress for re-enabling it.

**Content filters** (anime only) has independent **Hide adult / explicit titles** and **Hide Ecchi / sexual fan service** switches, both on by default. They apply to catalog results, search, cached items, and playback, and use AniList's adult flag and Ecchi genre, not scene analysis or an age rating. Classification can be incomplete.

### AniList rate limits

AniList allows 90 requests a minute and drops to 30 when degraded, which it currently is. StreamFin keeps every AniList query inside one shared budget of 25 a minute. Searches may use at most 15 of those and are dropped rather than queued, so browsing always has room. Identical queries in flight share one request. A listing's content checks go out as one batched query instead of one per title. When AniList is busy or answers 429, StreamFin serves the last good copy (kept for 24 hours) and honours Retry-After. A 429 from AniList is logged as a warning.

## Movies & TV

Movies & TV needs a free TMDB key: create one at themoviedb.org → Settings → API, then paste it into **Dashboard → Plugins → StreamFin → TMDB API key**. Either the short API key or the long read access token works. The channel stays hidden until a key is set.

Streams come from the FlixHQ sites (flixhqz.net first, then flixhq.ws), matched by title and year, trying each listed player in turn (Vidmoly, then DoodStream, then VidSrc, then the rest). Player pages are read as text; packed scripts are unpacked as a string transform and never run. Subtitle tracks the player offers become selectable subtitles in Jellyfin. TMDB's adult titles are always left out. Specials (season 0) and unaired episodes are not listed.

## Build

Requires Docker, Python 3, and Bash; a system-wide .NET installation is unnecessary.

```sh
bash scripts/fetch-references.sh
bash scripts/build.sh
```

AnimeHeaven uses the `curl` executable already included in the pinned Jellyfin image for bounded HTML requests; that site returned unrelated pages through .NET HTTP on the target host. Video still uses the C# relay. Page requests disable curl configuration, proxy use, and automatic redirects, and pin a validated public address. Other server installations need `curl` to enable AnimeHeaven.

The scripts pin both the Jellyfin image and .NET SDK image by digest. Reference assemblies stay in `.jellyfin/` and are never packaged or committed. The build runs dependency-free executable tests and produces `artifacts/StreamFin-0.5.0.0.zip`.

## Install

On mediastack, use `scripts/install-remote.py` (below). By hand:

1. Back up Jellyfin's configuration and stop Jellyfin.
2. Move previous AniFin or StreamFin versions outside the plugins directory. Extract the ZIP into `<config>/plugins/StreamFin_0.5.0.0/`. Make it readable by the Jellyfin service user.
3. Upgrading from AniFin: copy `plugins/configurations/Jellyfin.Plugin.AniFin.xml` to `Jellyfin.Plugin.StreamFin.xml`. Jellyfin names the settings file after the plugin assembly.
4. Start Jellyfin, then open **Dashboard → Plugins → StreamFin**.
5. Enable StreamFin and the sources you want, add a TMDB key for Movies & TV, select permitted users, and save. No user has access by default.
6. Run Jellyfin's **Refresh Channels** scheduled task if the channels have not appeared, then refresh the browser.

On mediastack, `<config>` is `~/mediastack/jellyfin/config` and Jellyfin runs as `1500:1500`. Keep the existing read-only container root and read-only media mount. StreamFin writes through Jellyfin's existing configuration/cache locations only.

`scripts/install-remote.py` runs as root with `--config`, `--artifact`, and `--backups` paths plus `--user marc`. It requires the tested server version, stops only Jellyfin, takes a consistent configuration backup, installs the package with service-user ownership, carries AniFin settings over, and restores the previous plugin/settings if startup fails. Upgrades preserve the existing enabled state and allowed users, including an empty access list; `--user` supplies the default access list only when that setting is absent. `--container` and `--owner` point it at a different Jellyfin container, such as a disposable test instance.

This is a private repository. Use manual installation; do not embed a GitHub token in a plugin repository URL.

To roll back, stop Jellyfin, move `plugins/StreamFin_0.5.0.0` outside the plugins directory, restore the previous version/settings from the installation backup, and start Jellyfin. No existing media files are changed.

The Anime tile shows a shuriken and the Movies & TV tile a popcorn bucket. Jellyfin Web uses a separate, fixed sidebar icon; `scripts/web-icon.py` applies a small CSS rule scoped to one channel, preserving existing branding CSS, and replaces AniFin's old rule. It draws the icon with a CSS clip path, so no embedded image permission is required. Run it on the server once per channel with `--url`, `--config`, `--user marc`, and either `--icon src/Assets/shuriken.svg --channel Anime` or `--icon src/Assets/popcorn.svg --channel "Movies & TV"`. The selected user needs an existing administrator session. Use `--remove` to remove just that channel's rule. It does not change native TV/mobile client icons.

## How the C# code fits together

You can work on sources without learning all of Jellyfin's APIs:

- `Models.cs` defines ordinary records and `IAnimeSource`: search titles, list episodes, and resolve video URLs. `IDirectAnimeSource` is for sources addressed by a catalog ID instead of a title search.
- `ZokoSource.cs` (HiAnime), `AnimeGgSource.cs` and `AnimeHeavenSource.cs` implement those operations. They parse HTML/data without evaluating website scripts. `SourceRegistry.cs` orders the sources, routes requests and isolates failures. To add another site, implement the interface and register the adapter.
- `AniListCatalog.cs` fetches anime metadata through the shared rate budget, with a bounded in-memory cache and a stale fallback. `TmdbCatalog.cs` does the same for movies and shows.
- `FlixSource.cs` finds a movie or episode on the FlixHQ sites and resolves a player to a stream.
- `OnlineSearch.cs` and `FilmSearch.cs` add online matches to Jellyfin's normal search and persist discovered titles for native navigation. Each title has a distinct grouping key, so Jellyfin does not collapse unrelated matches.
- `ContentAccess.cs` rechecks the content policy for cached titles and their descendants, batching a listing's lookups.
- `AnimeChannel.cs` and `FilmChannel.cs` translate those records into native Jellyfin folders, movies and episodes, with provider identifiers for native watch-state lookup.
- `StreamRelay.cs` gives Jellyfin a stable loopback URL and builds the media source Jellyfin probes. MP4 sources are re-resolved for each read/seek and streamed as byte ranges. HLS sources get a rewritten playlist: the best variant is kept, and every playlist, key and segment address is replaced by a loopback path, so FFmpeg never sees an upstream URL. Subtitles are attached as external text tracks that Jellyfin fetches server-side; Jellyfin only extracts embedded subtitles from local files.
- `SafeHttp.cs` makes every outbound request: named sites must match a list, embed players and CDNs (which rotate hostnames weekly) must be public web hosts. Every connection, including redirects, must resolve to a public address.
- `ChannelAccessFilter.cs` protects direct item/playback routes and cached list results in addition to hiding the channels from other users.
- `ArtworkAccess.cs` authorizes the web client's image requests using a scoped, encrypted cookie tied to a valid Jellyfin session.
- `Plugin.cs`, `ServiceRegistration.cs`, and `Configuration.html` are the Jellyfin plumbing and admin settings.

`Task<T>` means an asynchronous operation returning a `T`; `CancellationToken` allows navigation away or cancelled playback to stop the work. Constructor parameters are dependencies that Jellyfin supplies automatically. The adapter code is intentionally separate from that wiring.

## Playback and access

The browser uses its normal Jellyfin session. Playback goes through Jellyfin, retaining its player, progress reporting, and transcoding behavior. No source website scripts or advertising pages are embedded in the client.

The internal relay is not a general URL proxy: it accepts only opaque playback tickets, requires a random process credential (or, for subtitle files, a per-ticket key), permits only loopback callers, and checks that the ticket owner, the source and the content filter still allow playback, between chunks as well. Upstream addresses come only from the source's own playlist; private and local destinations are refused before connecting. No Jellyfin credentials are sent upstream.

Source websites can change or stop serving an episode. Use **Refresh catalog cache** after a source or configuration change. Diagnostics avoid writing source response bodies and session credentials to plugin logs. Jellyfin's own debug/transcoding logs may contain internal stream URLs; treat server logs as private.

## Tests

The executable tests cover parsing, numerical episode order, subtitle-only selection, the rate budget, HLS playlist rewriting, subtitle conversion, malformed responses, URL validation, and private-address rejection. `--provider-live` adds a real AnimeHeaven listing and byte-range fetch; `--hls-live` resolves HiAnime and FlixHQ (a movie and an episode) and fetches a real segment from each. Run the live checks on the Jellyfin host: stream links are tied to the network that resolved them.

`scripts/upgrade-smoke.py` checks an AniFin to StreamFin upgrade on **a disposable instance only**. `--phase before` runs with AniFin 0.4 installed, bootstraps test accounts and records watch progress. After installing 0.5 with `install-remote.py --container <test container> --owner <uid:gid>`, `--phase after` checks the plugin ID, settings, episode IDs, resume position and favourite survived, then plays HiAnime through Jellyfin's FFmpeg from the start and from 12:00, fetches the English subtitles through Jellyfin, and fires 60 searches to confirm browsing still answers afterwards.

`scripts/smoke.py` initializes **only a disposable instance** with test accounts, then checks the plugin, catalog placement, denied access, source resolution, and probing. Its protected `.secret` session file must never be committed.

```sh
python3 scripts/smoke.py --url http://TEST_SERVER:8096 --bootstrap
# Subsequent runs:
python3 scripts/smoke.py --url http://TEST_SERVER:8096
```

Live provider and browser tests are separate from deterministic build tests. The integration client uses Jellyfin 12's `Authorization: MediaBrowser … Token=…` syntax; query-string authentication is `ApiKey=`.

Verified on 2026-10-26 against an isolated copy of the exact server image: 133 parser/security/routing checks; live HiAnime, FlixHQ movie and FlixHQ episode streams; an AniFin 0.4 to StreamFin 0.5 upgrade keeping settings, IDs and progress; HiAnime transcoding from the start and after seeking; English subtitles served through Jellyfin; and a 60-search burst without AniList 429s. Movies & TV through Jellyfin itself still needs a TMDB key to test. TV clients still need testing.

`scripts/browser-smoke.cjs` exercises the browser flow after the API smoke test. Set `PLAYWRIGHT_MODULE` to an installed `playwright-core` module, `CHROMIUM_PATH` if needed, and `STREAMFIN_TEST_URL` to the disposable server. It reads the protected test session file and saves screenshots under `artifacts/`.

`scripts/search-smoke.py` checks remote title discovery, Japanese title aliases, both native search endpoints, and hiding previously cached Ecchi titles. `scripts/browser-search.cjs` checks the sidebar icon and search-to-playback flow using the saved disposable browser session. `scripts/browser-settings.cjs` verifies loading/saving permissions and filters, the clear-search button, and failed-load protection. `scripts/clear-search-smoke.py` checks administrator-only clearing and saved user data; rerun it with `--verify-after-restart` after restarting the disposable server to verify persistence and rediscovery. `scripts/providers-smoke.py` verifies source choices, AnimeHeaven playback probing, unauthorized access, independent enable switches, retained episode IDs/progress, and search with AnimeGG disabled. Run `scripts/browser-search.cjs` with `STREAMFIN_TEST_PROVIDER=AnimeHeaven` to verify a provider through native browser playback and seeking.

To empty a discovery list, use **Dashboard → Plugins → StreamFin → Clear search results**. This clears **Anime → Search results** and **Movies & TV → Search results** for all users. Watched status, favorites, resume positions, and episode IDs stay saved. Searching again adds matching titles back. The action requires an administrator and does not change your settings.

## Credits

The AnimeGG and AnimeHeaven adapters are adapted from Pal's MIT-licensed [Seanime providers](https://github.com/Seanime-contributions/Seanime-Providers/tree/main/src/anime). The HiAnime adapter follows the request flow [ani-cli](https://github.com/pystardust/ani-cli) uses, written independently. The FlixHQ adapter ports the author's own flixdl downloader. The shuriken and popcorn icons are from Material Design Icons. See `THIRD_PARTY_NOTICES.txt`. StreamFin is independent of all of these and of Jellyfin.
