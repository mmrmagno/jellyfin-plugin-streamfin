<p align="center"><img src="assets/banner.png" alt="StreamFin" width="600"></p>

<p align="center">
  <a href="https://github.com/mmrmagno/jellyfin-plugin-streamfin/releases/latest"><img alt="Release" src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fgithub.com%2Fmmrmagno%2Fjellyfin-plugin-streamfin%2Freleases%2Flatest%2Fdownload%2Fmanifest.json&query=%24%5B0%5D.versions%5B0%5D.version&label=release&prefix=v&color=aa5cc3"></a>
  <a href="#install"><img alt="Jellyfin version" src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fgithub.com%2Fmmrmagno%2Fjellyfin-plugin-streamfin%2Freleases%2Flatest%2Fdownload%2Fmanifest.json&query=%24%5B0%5D.versions%5B0%5D.targetAbi&label=Jellyfin&suffix=%20or%20newer&color=00a4dc"></a>
  <a href="https://github.com/mmrmagno/jellyfin-plugin-streamfin/actions/workflows/compat.yml"><img alt="Newest Jellyfin" src="https://img.shields.io/github/actions/workflow/status/mmrmagno/jellyfin-plugin-streamfin/compat.yml?branch=main&label=newest%20Jellyfin"></a>
  <br>
  <a href="https://github.com/mmrmagno/jellyfin-plugin-streamfin/actions/workflows/build.yml"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/mmrmagno/jellyfin-plugin-streamfin/build.yml?branch=main&label=build"></a>
  <a href="https://github.com/mmrmagno/jellyfin-plugin-streamfin/actions/workflows/security.yml"><img alt="Security" src="https://img.shields.io/github/actions/workflow/status/mmrmagno/jellyfin-plugin-streamfin/security.yml?branch=main&label=security"></a>
  <a href="https://scorecard.dev/viewer/?uri=github.com/mmrmagno/jellyfin-plugin-streamfin"><img alt="OpenSSF Scorecard" src="https://api.scorecard.dev/projects/github.com/mmrmagno/jellyfin-plugin-streamfin/badge"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-GPL--2.0-blue"></a>
</p>

A Jellyfin plugin that streams anime, movies, TV and music instead of storing them. They
show up as three channels, **Anime**, **Movies & TV** and **Music**, and play in Jellyfin's
own player, with progress, resume and subtitles working like they do for everything else in
the library. Music is lossless FLAC.

Nothing gets downloaded ahead of time. StreamFin finds the stream when you press play, and
Jellyfin plays it through a small relay inside the plugin. The one exception is music: songs
that have been played are kept on the server, up to a set size, so skipping and replaying
are instant.

## Why

I host my own Jellyfin, but I do not have the storage to keep every movie, show and anime
I might want to watch. Most of them I watch once anyway. Downloading something, waiting,
and deleting it again to make room got old quickly.

So the server finds it for me when I want it. The library on disk is for the things I want
to keep, and everything else streams. It works on the TV too, which a streaming site in a
browser tab full of ads never did.

It started as AniFin, anime only. When the anime sources started breaking I rewrote the
source layer, added movies and TV while I was at it, and renamed it StreamFin.

## What it does

- **Anime**: Popular, This Season and A to Z, from AniList. Pick a title, a source, then
  an episode. Japanese audio with English subtitles only, never a dub.
- **Movies & TV**: Popular movies, Popular TV and Trending, from TMDB. Movies play
  directly, shows open into seasons and episodes.
- **Music**: lossless FLAC, CD quality (16 bit, 44.1 kHz) and on some albums 24 bit hi-res, from
  [Monochrome](https://github.com/monochrome-music/monochrome)'s catalog. Search for an
  artist, album or song. Albums open as real album pages with numbered tracks, artists
  open into their top tracks, albums and singles, **Starred** holds what you star in a
  music app, and **Recently played** remembers what you listened to. The FLAC reaches the player as the catalog serves it, never transcoded.
  Lyrics, synced where they exist, come from [LRCLIB](https://lrclib.net). When a song is
  also published as a clean edit, the edit is hidden and the original plays. Played songs
  are kept on the server (4 GB by default, **Song cache** in the settings, 0 turns it off),
  so skipping within a song and playing it again do not wait for the catalog.
- **Search**: Jellyfin's normal search also looks in the online catalogs, so it finds
  titles nobody has browsed yet. Found titles stay under each channel's Search results.
- **Access**: only the users ticked in the settings see the channels.

| Source | Finds the show by | Subtitles |
|---|---|---|
| HiAnime | MyAnimeList ID, no title guessing | Separate English track |
| AnimeGG | Title search | Burned in |
| AnimeHeaven | Title search | Burned in |
| FlixHQ (movies and TV) | Title and year | Usually a separate English track |
| Monochrome (music) | Its own catalog IDs, with ISRCs | Not applicable |

If a source fails or is missing an episode, go back and pick another one. The message says
which it was: HiAnime, for one, tells an episode it does not have apart from a change on its site.

## Install

Needs Jellyfin **12.3** or newer.

1. In Jellyfin, open **Dashboard > Plugins > Repositories** and add this one:
   ```
   https://github.com/mmrmagno/jellyfin-plugin-streamfin/releases/latest/download/manifest.json
   ```
2. Install **StreamFin** from the catalog (it is under Channels) and restart Jellyfin.
3. Open **Dashboard > Plugins > StreamFin**, turn it on, and tick the users who are
   allowed in. Nobody is by default.
4. For Movies & TV, make a free account on themoviedb.org, go to **Settings > API**, and
   paste the key. The short API key and the long read access token both work.

The channels then show up on the home screen and in the sidebar, next to the libraries.
If one is missing, check that your user is ticked and the TMDB key is saved, then reload
the app. Music needs no key. Under **Music catalog address** you can point it at another
instance of the same catalog API. Updates come through the same repository like any other plugin.

The web app's sidebar gives channels a generic icon. For the shuriken, the popcorn and the note,
make an API key under **Dashboard > API Keys** and run this from a clone of the repo:

```sh
JELLYFIN_TOKEN=<key> python3 scripts/web-icon.py --url https://your.jellyfin --channel Anime
JELLYFIN_TOKEN=<key> python3 scripts/web-icon.py --url https://your.jellyfin --channel "Movies & TV"
JELLYFIN_TOKEN=<key> python3 scripts/web-icon.py --url https://your.jellyfin --channel Music
```

In the web app, found artists are under the Music channel's Search results rather than
in the search page's Artists row, which only lists the local library.

### Music apps (Subsonic)

Music also speaks OpenSubsonic, so apps like Symfonium, Feishin, Tempo or play:Sub get
gapless playback, queues, offline downloads and synced lyrics. Under **Subsonic apps** in
the settings, turn on the API and press **New password** next to a user. In the app, pick
Subsonic or OpenSubsonic and enter:

- **Server**: your Jellyfin address followed by `/StreamFin`, for example
  `https://your.jellyfin/StreamFin`
- **Username**: the Jellyfin username
- **Password**: the app password from the settings, not the Jellyfin one

Subsonic apps put their credentials in the address of every request, as Jellyfin's own
apps do with their tokens, so they can show up in Jellyfin's log and in reverse proxy logs.
Keep those logs private. If an app password may have leaked, press **New password** to
revoke it.

## How it works

The catalogs and the video are separate. AniList and TMDB provide titles, posters and
episode lists. The sources only have to turn "this episode" into a video URL.

Jellyfin never sees that URL. It gets a loopback address into the plugin with a one-off
ticket, and the plugin fetches the video from the site. MP4 is streamed as byte ranges, and
the link is only looked up again if it expires. HLS gets its playlist rewritten so every
segment points back at the relay, which means FFmpeg only ever talks to localhost.

Music takes the short path through the same relay. The catalog serves each track as one
FLAC file with byte ranges, so the plugin offers it for direct play. Jellyfin's own static
stream fetches it through the relay and hands the bytes to the player unchanged, with
seeking. A transcode would follow whatever the client asks for, which is usually AAC or
MP3, so the plugin avoids one.

The relay is not a general proxy. It only accepts its own tickets, only from loopback, and
every outgoing request has to land on a public address. Nothing from the sites is ever run.

## Building it

Needs Docker, Python 3 and Bash, no local .NET:

```sh
bash scripts/fetch-references.sh
bash scripts/build.sh
```

That runs the tests and writes the plugin zip to `artifacts/`.

CI does the same on every push, plus CodeQL, a secret scan over the whole history and a
lint of the workflows. Every Monday it also loads the plugin on the newest Jellyfin, so a
breaking release shows up before a server updates into it. Everything that parses a
third-party page is fuzzed on every pull request and for an hour every Sunday.

## Credits

The AnimeGG and AnimeHeaven adapters are adapted from the MIT-licensed
[Seanime providers](https://github.com/Seanime-contributions/Seanime-Providers). The
HiAnime flow follows what [ani-cli](https://github.com/pystardust/ani-cli) does, written
from scratch. FlixHQ is a port of my own downloader. Icons are from Material Design Icons.
Details in `THIRD_PARTY_NOTICES.txt`.

StreamFin does not host anything. It plays what third-party sites already serve, and
whether that is legal where you live is on you.

I write about my projects at [marc-os.com](https://marc-os.com).

## License

[GPL-2.0](LICENSE).
