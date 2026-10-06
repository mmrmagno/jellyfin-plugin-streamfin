# AniFin

An online anime channel for Jellyfin. Open **Anime**, browse **Popular**, **This Season**, or **A–Z**, choose a title, then play an episode with Jellyfin's own player. Use Jellyfin's **magnifying glass** to search the online catalog, including titles you have not browsed yet. Discovered titles also appear in **Anime → Search results**.

AniFin uses AniList for catalog metadata and contacts AnimeGG and AnimeHeaven directly for subtitled streams. It does not run Seanime, download episodes into your media library, or use torrents/debrid. Jellyfin can temporarily buffer or transcode during playback.

## Compatibility

This build targets Jellyfin **12.1.0**, specifically `jellyfin/jellyfin:12.1.20260915-010956`. It is compiled against that image's API assemblies with .NET 10. Other server versions need a matching build and testing. Browser playback is verified, and phone playback with AnimeGG was confirmed by the user. TV clients still need testing.

Both providers’ English subtitles are burned into the video, so there is no separate subtitle switch. AnimeGG selects its highest available quality; AnimeHeaven uses the site’s source quality. Catalog listings do not guarantee source availability. If a title match is ambiguous, select the correct source title.

Open a title and choose **Episodes · AnimeGG** or **Episodes · AnimeHeaven**. Each provider has an independent switch under **Dashboard → Plugins → AniFin**. If one fails or lacks an episode, return to the title and choose the other. Switching is manual: AniFin does not swap different video files partway through playback. Existing AnimeGG episode IDs stay unchanged. Disabling a source hides its cached items and denies playback while retaining saved progress for re-enabling it.

Online search accepts 2–100 characters, returns up to 30 matches per query, supports AniList's English/Japanese title matching, and merges results with normal library search. Searches scoped to unrelated libraries remain local. AniList errors/timeouts leave local search available.

**Dashboard → Plugins → AniFin → Content filters** has independent **Hide adult / explicit titles** and **Hide Ecchi / sexual fan service** switches, both enabled by default. Filters apply to catalog results, search, cached items, and playback. They use AniList's adult flag and Ecchi genre, not scene analysis or a comprehensive age rating. Classification can be incomplete.

To empty the discovery list, use **Dashboard → Plugins → AniFin → Clear search results**. This clears **Anime → Search results** for all AniFin users. Watched status, favorites, resume positions, and episode IDs stay saved. Searching again adds matching titles back. The action requires an administrator and does not change your settings.

## Build

Requires Docker, Python 3, and Bash; a system-wide .NET installation is unnecessary.

```sh
bash scripts/fetch-references.sh
bash scripts/build.sh
```

AnimeHeaven uses the `curl` executable already included in the pinned Jellyfin image for bounded HTML requests; that site returned unrelated pages through .NET HTTP on the target host. Video still uses the C# relay. Page requests disable curl configuration, proxy use, and automatic redirects, and pin a validated public address. Other server installations need `curl` to enable AnimeHeaven.

The scripts pin both the Jellyfin image and .NET SDK image by digest. Reference assemblies stay in `.jellyfin/` and are never packaged or committed. The build runs dependency-free executable tests and produces `artifacts/AniFin-0.4.0.0.zip`.

## Install

1. Back up Jellyfin's configuration and stop Jellyfin.
2. Move previous AniFin versions outside the plugins directory. Extract the ZIP into `<config>/plugins/AniFin_0.4.0.0/`. Make it readable by the Jellyfin service user.
3. Start Jellyfin, then open **Dashboard → Plugins → AniFin**.
4. Enable the desired providers and the channel, select permitted users, and save. No user has access by default.
5. Run Jellyfin's **Refresh Channels** scheduled task if Anime has not appeared, then refresh the browser.

On mediastack, `<config>` is `~/mediastack/jellyfin/config` and Jellyfin runs as `1500:1500`. Keep the existing read-only container root and read-only media mount. AniFin writes through Jellyfin's existing configuration/cache locations only.

This is a private repository. Use manual installation; do not embed a GitHub token in a plugin repository URL.

To roll back, stop Jellyfin, move `plugins/AniFin_0.4.0.0` outside the plugins directory, restore the previous version/settings from the installation backup, and start Jellyfin. No existing media files are changed.

The channel tile includes a shuriken image. Jellyfin Web uses a separate, fixed sidebar icon; `scripts/web-icon.py` applies a small CSS rule scoped to the Anime channel, preserving existing branding CSS. It draws the icon with a CSS clip path, so no embedded image permission is required. Run it on the server with `--url`, `--config`, `--user marc`, and `--icon src/Assets/shuriken.svg`. The selected user needs an existing administrator session. Use `--remove` to remove just this rule. It does not change native TV/mobile client icons.

## How the C# code fits together

You can work on providers without learning all of Jellyfin's APIs:

- `Models.cs` defines ordinary records and `IAnimeSource`: search titles, list episodes, and resolve video URLs.
- `AnimeGgSource.cs` and `AnimeHeavenSource.cs` implement those three operations. They parse HTML/data without evaluating website scripts. `SourceRegistry.cs` routes requests and isolates provider failures. To add another site, implement this interface and register the adapter.
- `AniListCatalog.cs` fetches title metadata and keeps a bounded in-memory cache. Catalog pages are fetched on demand rather than importing the entire catalog.
- `OnlineSearch.cs` adds online matches to Jellyfin's normal search and persists discovered series for native navigation. Each series has a distinct grouping key, so Jellyfin does not collapse unrelated matches.
- `ContentAccess.cs` rechecks the content policy for cached titles and their descendants.
- `AnimeChannel.cs` translates those records into native Jellyfin folders and episodes, with provider identifiers for native watch-state lookup.
- `StreamRelay.cs` gives Jellyfin a stable loopback URL. It resolves the source again for each read/seek, follows only validated public destinations, and streams byte ranges with bounded memory. FFprobe supplies actual media stream information to Jellyfin.
- `ChannelAccessFilter.cs` protects direct item/playback routes and cached list results in addition to hiding the channel from other users.
- `ArtworkAccess.cs` authorizes the web client's image requests using a scoped, encrypted cookie tied to a valid Jellyfin session.
- `Plugin.cs`, `ServiceRegistration.cs`, and `Configuration.html` are the Jellyfin plumbing and admin settings.

`Task<T>` means an asynchronous operation returning a `T`; `CancellationToken` allows navigation away or cancelled playback to stop the work. Constructor parameters are dependencies that Jellyfin supplies automatically. The adapter code is intentionally separate from that wiring.

## Playback and access

The browser uses its normal Jellyfin session. Playback goes through Jellyfin, retaining its player, progress reporting, and transcoding behavior. No source website scripts or advertising pages are embedded in the client.

The internal relay is not a general URL proxy: it accepts only opaque episode tickets, requires a random process credential, permits only loopback callers, and checks that the ticket owner remains authorized. Website and CDN destinations are restricted; DNS addresses and redirects are checked before connecting. No Jellyfin credentials are sent upstream.

Source websites can change or stop serving an episode. Use **Refresh catalog cache** after a provider/configuration change. Diagnostics avoid writing source response bodies and session credentials to plugin logs. Jellyfin's own debug/transcoding logs may contain internal stream URLs; treat server logs as private.

## Tests

The executable tests cover parsing, numerical episode order, subtitle-only selection, malformed responses, URL validation, and private-address rejection. `scripts/smoke.py` initializes **only a disposable instance** with test accounts, then checks the plugin, catalog placement, denied access, source resolution, and probing. Its protected `.secret` session file must never be committed.

```sh
python3 scripts/smoke.py --url http://TEST_SERVER:8096 --bootstrap
# Subsequent runs:
python3 scripts/smoke.py --url http://TEST_SERVER:8096
```

Live provider and browser tests are separate from deterministic build tests. The integration client uses Jellyfin 12's `Authorization: MediaBrowser … Token=…` syntax.

Verified on 2026-10-06 against an isolated copy of the exact server image: 68 parser/security/content-policy/routing checks; native Chromium catalog navigation and playback; seeking to two minutes; pause/resume; saved resume position and watched state; and denied cached-item/playback access after revocation. Phone playback with AnimeGG was also confirmed by the user. TV clients and watch-state sharing across catalog categories/providers still need testing.

`scripts/browser-smoke.cjs` exercises the browser flow after the API smoke test. Set `PLAYWRIGHT_MODULE` to an installed `playwright-core` module, `CHROMIUM_PATH` if needed, and `ANIFIN_TEST_URL` to the disposable server. It reads the protected test session file and saves screenshots under `artifacts/`.

`scripts/search-smoke.py` checks remote title discovery, Japanese title aliases, both native search endpoints, and hiding previously cached Ecchi titles. `scripts/browser-search.cjs` checks the sidebar icon and search-to-playback flow using the saved disposable browser session. `scripts/browser-settings.cjs` verifies loading/saving permissions and filters, the clear-search button, and failed-load protection. `scripts/clear-search-smoke.py` checks administrator-only clearing and saved user data; rerun it with `--verify-after-restart` after restarting the disposable server to verify persistence and rediscovery.

For the existing mediastack host, `scripts/install-remote.py` runs as root with `--config`, `--artifact`, and `--backups` paths plus `--user marc`. It requires the tested server version, stops only Jellyfin, takes a consistent configuration backup, installs the package with service-user ownership, and restores the previous plugin/settings if startup fails. Do not use the disposable account bootstrap script on production.

## Credits

The AnimeGG and AnimeHeaven adapters are adapted from Pal's MIT-licensed [Seanime providers](https://github.com/Seanime-contributions/Seanime-Providers/tree/main/src/anime). See `THIRD_PARTY_NOTICES.md`. AniFin is independent of Seanime and Jellyfin.

`scripts/providers-smoke.py` verifies both source choices, AnimeHeaven playback probing, unauthorized access, independent enable switches, retained episode IDs/progress, and search with AnimeGG disabled. Run `scripts/browser-search.cjs` with `ANIFIN_TEST_PROVIDER=AnimeHeaven` to verify the new provider through native browser playback and seeking. The executable tests accept `--provider-live` for an optional real AnimeHeaven listing and bounded byte-range fetch.
