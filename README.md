# StreamFin

A Jellyfin plugin that adds online anime, movies and TV to my server. They show up as
two channels, **Anime** and **Movies & TV**, and play in Jellyfin's own player, with
progress, resume and subtitles working like they do for anything else in the library.

Nothing gets downloaded into the library. StreamFin finds the stream when I press play
and Jellyfin plays it through a small relay inside the plugin.

## Why

I already had a Jellyfin server, and every time I wanted to watch an anime or a movie I
did not own, I ended up on some streaming site in a browser tab full of ads. On the TV
that was not even an option.

So I wrote a plugin that does the browsing and the scraping for me. It started as AniFin,
anime only. When the anime sources started breaking I rewrote the source layer, added
movies and TV while I was at it, and renamed it StreamFin.

## What it does

- **Anime**: Popular, This Season and A to Z, from AniList. I pick a title, a source,
  then an episode. Japanese audio with English subtitles only, never a dub.
- **Movies & TV**: Popular movies, Popular TV and Trending, from TMDB. Movies play
  directly, shows open into seasons and episodes.
- **Search**: Jellyfin's normal search also looks in both online catalogs, so I can find
  a title I have never browsed. Found titles stay under each channel's Search results.
- **Access**: only users I tick in the settings see the channels. Adult titles are
  hidden by default, and so is Ecchi.

### Sources

| Source | Finds the show by | Subtitles |
|---|---|---|
| HiAnime | MyAnimeList ID, no title guessing | Separate English track |
| AnimeGG | Title search | Burned in |
| AnimeHeaven | Title search | Burned in |
| FlixHQ (movies and TV) | Title and year | Whatever the player offers |

If a source fails or is missing an episode, I go back and pick another one. Each source
has its own switch in the plugin settings.

## Install

It targets Jellyfin **12.3.0** and is built against that exact image, so other versions
need a rebuild. Building needs Docker, Python 3 and Bash, no local .NET.

```sh
bash scripts/fetch-references.sh
bash scripts/build.sh
```

That runs the tests and writes `artifacts/StreamFin-0.5.0.0.zip`. To install by hand,
stop Jellyfin, extract the zip into `<config>/plugins/StreamFin_0.5.0.0/`, start it again,
then open **Dashboard > Plugins > StreamFin**:

1. Turn it on and pick the sources.
2. Paste a TMDB key for Movies & TV. The free one from themoviedb.org works, either the
   short API key or the long read access token. Without a key that channel stays hidden.
3. Tick the users who are allowed in. Nobody is by default.

On my server I use the install script instead. It checks the Jellyfin version, backs up
the config, installs, and puts the old version back if Jellyfin does not come up:

```sh
sudo python3 -I scripts/install-remote.py \
  --config <jellyfin config dir> \
  --artifact artifacts/StreamFin-0.5.0.0.zip \
  --backups <backup dir> --user <jellyfin user>
```

Coming from AniFin, it also carries the settings over. The plugin ID and the channel name
did not change, so watch progress and favourites survive the rename.

The sidebar icons in Jellyfin Web (a shuriken for Anime, popcorn for Movies & TV) come
from `scripts/web-icon.py`, which adds a small CSS rule per channel.

## How it works

The catalogs and the video are separate. AniList and TMDB give me titles, posters and
episode lists. The sources only have to turn "this episode" into a video URL.

Jellyfin never sees that URL. It gets a loopback address into the plugin with a one-off
ticket, and the plugin fetches the video from the site. MP4 is streamed as byte ranges
and re-resolved on every seek. HLS gets its playlist rewritten so every segment points
back at the relay, which means FFmpeg only ever talks to localhost.

The relay is not a general proxy. It only accepts its own tickets, only from loopback,
and checks on every chunk that the user is still allowed to watch it. Every outgoing
request has to land on a public address, redirects included, and player pages are read
as text. Packed JavaScript gets unpacked as a string, it is never run.

## The parts that were not obvious

- **The 429s were not the video sites.** AniList is in a degraded mode at 30 requests a
  minute, and the plugin was asking about every title one by one. Now everything shares
  one budget of 25 a minute, searches get at most 15 and are dropped instead of queued,
  and a busy AniList means the last good answer from cache.
- **FFmpeg refuses HLS segments with odd extensions.** Some CDNs serve video segments as
  `.jpg`. The relay renames them to something FFmpeg accepts.
- **Jellyfin does not extract subtitles from remote streams**, only from local files. The
  subtitle tracks get served as separate VTT files instead.
- **TV apps load images without logging in.** The web app sends credentials for artwork,
  Android TV and Chromecast do not, so the covers were missing on the TV. Artwork is now
  public like it is for library items. Everything else still needs a permitted user.
- **Sources die.** AllAnime and animepahe were the obvious picks and both now need a real
  browser to get past their protection, so they are not in here.

## Tests

`bash scripts/build.sh` runs 133 checks: parsers, episode order, the rate budget,
playlist rewriting, subtitle conversion and the URL and address rules. `--provider-live`
and `--hls-live` fetch from the real sites. Stream links are tied to the network that
resolved them, so I run those on the server.

The scripts in `scripts/` (`smoke.py`, `upgrade-smoke.py` and friends) drive a throwaway
Jellyfin instance through login, search, playback, seeking and an AniFin upgrade. Never
point them at a real server, they create test accounts.

## Credits

The AnimeGG and AnimeHeaven adapters are adapted from the MIT-licensed
[Seanime providers](https://github.com/Seanime-contributions/Seanime-Providers). The
HiAnime flow follows what [ani-cli](https://github.com/pystardust/ani-cli) does, written
from scratch. FlixHQ is a port of my own downloader. Icons are from Material Design Icons.
Details in `THIRD_PARTY_NOTICES.txt`.

I write about my projects at [marc-os.com](https://marc-os.com).

## License

[GPL-2.0](LICENSE).
