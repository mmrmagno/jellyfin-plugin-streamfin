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
  The catalog goes down now and then. Albums, artists and covers it has answered for are kept
  on disk and open at once, songs in the song cache keep playing, and songs you played,
  starred or put in a playlist keep their stars, scrobbles and lyrics until it is back.
  Meanwhile searches, albums and artists come from [Deezer](https://www.deezer.com)'s public
  catalog: descriptions and covers only, no account, no audio. A song found there plays the
  catalog's FLAC of the same recording (matched by ISRC) once the catalog answers again, or
  YouTube's copy until then. Untick **Search Deezer when the catalog cannot** to turn it off.
  A song the catalog cannot send can play from YouTube instead. That is off by default: tick
  **Play from YouTube when the catalog cannot**. It picks the artist's own upload of the same
  length and plays it as AAC at about 128 kbps. That is not lossless, and the song's details say
  so. Untick **Allow lossy sources** to only ever play FLAC. A song kept from YouTube changes to
  the FLAC once the catalog is back: the next time it plays, or in the background for starred
  songs, songs in playlists and songs played more than once (Scheduled Tasks, **Upgrade kept
  songs to FLAC**, hourly, a few songs at a time). YouTube refuses many data centre addresses
  ("Sign in to confirm you're not a bot"). If your server's is one, set **YouTube proxy** to an
  http or socks5 proxy with another address, such as a VPN container next to Jellyfin
  (`http://user:password@vpn:8888`). Only YouTube goes through it.
- **Radio**: **Instant Mix** on an artist, album or song (and the **Radio** folder in channel
  views that show folders) mixes the artist's top tracks with those of similar artists, as [ListenBrainz](https://listenbrainz.org) listeners play them
  (the artist is found on [MusicBrainz](https://musicbrainz.org) by the ISRC of one of its
  songs). Music apps get the same mix as "similar songs" or instant mix, and the similar
  artists on the artist page. For artists ListenBrainz does not know, a free Last.fm API key in
  the settings adds Last.fm's similar artists.
- **Save to library**: set a **Music library folder** in the settings and every album you star
  (a heart in Jellyfin, a star in a music app) is saved there as FLAC with its cover, as
  `Artist/Album (Year)/01 Title.flac`, tagged with that album even when the catalog's file names another. Add that folder to a Jellyfin music library and the
  albums play offline and in any Jellyfin music app, Finamp included. Saving is slow on purpose,
  one song every 20 seconds, so the catalog does not block the server. Existing files are never
  replaced, and unstarring an album stops its save but keeps what was already saved.
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
| Deezer (music catalog, when Monochrome cannot) | Its own IDs, with ISRCs; songs play from Monochrome by ISRC, or from YouTube | Not applicable |
| YouTube (music, when Monochrome cannot) | Artist and title, same length within 3 s, the artist's own upload first | Not applicable |

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

The catalog has no list of everything, so the library an app syncs is yours: what you played,
starred or put in a playlist, plus every album of the artists you starred or keep
playing. Ratings, play counts and the play queue are saved on the server, so another device
picks up where you stopped.

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

When the catalog takes longer than 6 seconds to search, the search goes to Deezer instead, and the
same words keep getting Deezer's results for half an hour, so paging through them stays steady.
Deezer's songs, albums and artists carry their own IDs (`dz` and a number), so nothing stored
under a catalog ID changes. To play one, the plugin searches the catalog for the same ISRC (or
the same title, artist and length) and streams that FLAC.

When the catalog does not answer within 20 seconds and the YouTube fallback is on, the plugin
asks [yt-dlp](https://github.com/yt-dlp/yt-dlp) for the song instead. yt-dlp needs
[deno](https://deno.com) to read YouTube's player code. The plugin downloads both from their
official GitHub releases into its own data folder. It checks each file against the SHA-256
published in the same release and never runs a file that does not match. A scheduled task,
**Update YouTube tools**, updates them daily. If you would rather install them yourself, set
their paths in the settings. yt-dlp downloads the song itself into the song cache, and it plays
from there. Behind a proxy, the audio address YouTube hands out only works inside the yt-dlp run
that asked for it, so nothing else could fetch it. A new song from YouTube takes 15 to 20
seconds to start; after that it plays from disk. yt-dlp comes as its unpacked build, which
starts twice as fast as the single file. The zip is checked before it is unpacked, and every
file in it has to stay inside its folder.

The catalog goes down for minutes at a time, and on a bad day it comes back for two minutes
and goes again. Three failures within two minutes, or three answers slower than 8 seconds (5
for audio), count it as down. From then on no click waits on it: new songs go straight to
YouTube, and anything seen before comes from the copies the plugin keeps. In the background the
plugin checks it again, after 30 seconds at first and then after longer pauses, up to 15
minutes. It is used again only after three quick answers in a row, 20 seconds apart. It is
also checked once when Jellyfin starts, so a server that comes up during an outage knows at once.

Under **Sources** in the settings, each source has its own switch, its live state, and a
**Test now** button for Monochrome and YouTube. Switching Monochrome off stops songs playing
from it, while its catalog still answers searches and albums.

The relay is not a general proxy. It only accepts its own tickets, only from loopback, and
every outgoing request has to land on a public address. Nothing from the sites is ever run,
with one exception: when the YouTube fallback is on, yt-dlp runs YouTube's player code in
deno to find the audio address. yt-dlp makes its own requests to YouTube, so those skip the
plugin's address checks. With a YouTube proxy set, every yt-dlp request goes through it, and its
password never appears in a log or message. The checksums come from the same GitHub release as the files, so they
catch a broken or swapped download, but they do not prove who built it. Turning the fallback on
means running each new yt-dlp and deno release as the Jellyfin user, a day after it comes out at
most. The downloads are the glibc builds, which suit the official Jellyfin image; on Alpine, set
the paths to your own.

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
