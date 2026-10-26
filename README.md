<p align="center"><img src="assets/banner.png" alt="StreamFin" width="600"></p>

A Jellyfin plugin that streams anime, movies and TV instead of storing them. They show up
as two channels, **Anime** and **Movies & TV**, and play in Jellyfin's own player, with
progress, resume and subtitles working like they do for everything else in the library.

Nothing gets downloaded. StreamFin finds the stream when I press play, and Jellyfin plays
it through a small relay inside the plugin.

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

- **Anime**: Popular, This Season and A to Z, from AniList. I pick a title, a source, then
  an episode. Japanese audio with English subtitles only, never a dub.
- **Movies & TV**: Popular movies, Popular TV and Trending, from TMDB. Movies play
  directly, shows open into seasons and episodes.
- **Search**: Jellyfin's normal search also looks in both online catalogs, so I can find a
  title I have never browsed. Found titles stay under each channel's Search results.
- **Access**: only the users I tick in the settings see the channels. Adult titles are
  hidden by default, and so is Ecchi.

| Source | Finds the show by | Subtitles |
|---|---|---|
| HiAnime | MyAnimeList ID, no title guessing | Separate English track |
| AnimeGG | Title search | Burned in |
| AnimeHeaven | Title search | Burned in |
| FlixHQ (movies and TV) | Title and year | Whatever the player offers |

If a source fails or is missing an episode, I go back and pick another one. Each source has
its own switch in the settings.

## Install

Needs Jellyfin **12.3** or newer.

1. In Jellyfin, open **Dashboard > Plugins > Repositories** and add this one:
   ```
   https://raw.githubusercontent.com/mmrmagno/jellyfin-plugin-streamfin/main/manifest.json
   ```
2. Install **StreamFin** from the catalog (it is under Channels) and restart Jellyfin.
3. Open **Dashboard > Plugins > StreamFin**, turn it on, and tick the users who are
   allowed in. Nobody is by default.
4. For Movies & TV, paste a TMDB key. The free one from themoviedb.org (Settings > API)
   works, either the short API key or the long read access token. Without a key that
   channel stays hidden.

Updates arrive through the same repository like any other plugin.

The sidebar icons in Jellyfin Web, a shuriken for Anime and popcorn for Movies & TV, are
optional. `scripts/web-icon.py` adds a small CSS rule for each channel.

## How it works

The catalogs and the video are separate. AniList and TMDB give me titles, posters and
episode lists. The sources only have to turn "this episode" into a video URL.

Jellyfin never sees that URL. It gets a loopback address into the plugin with a one-off
ticket, and the plugin fetches the video from the site. MP4 is streamed as byte ranges and
re-resolved on every seek. HLS gets its playlist rewritten so every segment points back at
the relay, which means FFmpeg only ever talks to localhost.

The relay is not a general proxy. It only accepts its own tickets, only from loopback, and
checks on every chunk that the user is still allowed to watch. Every outgoing request has to
land on a public address, redirects included, and player pages are read as text. Packed
JavaScript gets unpacked as a string, it is never run.

## Building it

Needs Docker, Python 3 and Bash, no local .NET:

```sh
bash scripts/fetch-references.sh
bash scripts/build.sh
```

That runs the tests and writes the plugin zip to `artifacts/`.

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
