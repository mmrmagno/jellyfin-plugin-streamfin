using System.Text;
using StreamFin;
using static Harness;

// Playlist import: what a paste is, each source's reader, and where an imported playlist came from.
static class ImportChecks
{
    public static async Task RunAsync()
    {
        const string id = "37i9dQZF1DX4UtSsGT1Sbe";
        Check(ImportReaders.Detect("https://open.spotify.com/playlist/" + id + "?si=x") == (ImportKind.SpotifyPlaylist, id)
            && ImportReaders.Detect("https://open.spotify.com/intl-de/album/" + id) == (ImportKind.SpotifyAlbum, id)
            && ImportReaders.Detect("spotify:playlist:" + id) == (ImportKind.SpotifyPlaylist, id)
            && ImportReaders.Detect("https://music.youtube.com/playlist?list=PLabcdefghij12") == (ImportKind.YouTube, "PLabcdefghij12")
            && ImportReaders.Detect("https://www.deezer.com/fr/playlist/908622995") == (ImportKind.Deezer, "908622995"),
            "playlist links from Spotify, YouTube Music and Deezer are recognised");
        Check(ImportReaders.Detect("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC\nhttps://open.spotify.com/track/7GhIk7Il098yCjg4BQjzvb").Kind == ImportKind.SpotifyTracks
            && ImportReaders.Detect("Track Name,Artist Name(s)\nOne,Two").Kind == ImportKind.Csv && ImportReaders.Detect("Daft Punk - One More Time").Kind == ImportKind.Text
            && ImportReaders.Detect("https://example.com/whatever").Kind == ImportKind.None && ImportReaders.Detect("  ").Kind == ImportKind.None,
            "copied Spotify songs, a CSV, a song list and anything else are told apart");
        Check(ImportReaders.FromSource(ImportReaders.SourceOf(ImportKind.SpotifyAlbum, id)) == (ImportKind.SpotifyAlbum, id)
            && ImportReaders.FromSource("deezer:908622995") == (ImportKind.Deezer, "908622995") && ImportReaders.FromSource("youtube:PLabcdefghij12") == (ImportKind.YouTube, "PLabcdefghij12")
            && ImportReaders.FromSource("spotify:track:" + id) is null && ImportReaders.FromSource("deezer:../x") is null && ImportReaders.FromSource(null) is null
            && ImportReaders.SourceOf(ImportKind.Text, "") is null, "only playlists read from a link can be read again, and a stored source is checked");

        var embed = "<html><script id=\"__NEXT_DATA__\" type=\"application/json\">{\"props\":{\"pageProps\":{\"state\":{\"data\":{\"entity\":{\"name\":\"All Out 80s\",\"trackList\":["
            + "{\"title\":\"Thriller\",\"subtitle\":\"Michael Jackson\",\"duration\":358807},{\"title\":\"September\",\"subtitle\":\"Earth, Wind & Fire\",\"duration\":215000},{\"title\":\"Under Pressure\",\"subtitle\":\"Queen,\\u00a0David Bowie\",\"duration\":248440},{\"title\":\"\",\"subtitle\":\"x\"}]}}}}}}</script></html>";
        var (name, tracks) = ImportReaders.ParseSpotifyEmbed(embed);
        Check(name == "All Out 80s" && tracks is [{ Title: "Thriller", Artist: "Michael Jackson", DurationMs: 358807 }, { Artist: "Earth, Wind & Fire" }, { Artist: "Queen" }],
            "Spotify's embed page gives each song with its first artist (a name with a comma kept whole) and length");
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"content\":{\"totalCount\":150,\"items\":[]}}"));
        Check(ImportReaders.SpotifyTotal("<script id=\"initialState\" type=\"text/plain\">" + state + "</script>") == 150 && ImportReaders.SpotifyTotal("<html></html>") is null,
            "the playlist page says how many songs there really are");
        var page = "<meta property=\"og:title\" content=\"Never Gonna Give You Up\"/><meta property=\"og:description\" content=\"Rick Astley · Whenever You Need Somebody · Song · 1987\"/>"
            + "<meta name=\"music:duration\" content=\"214\"/><meta name=\"music:musician_description\" content=\"Rick Astley\"/>";
        Check(ImportReaders.ParseSpotifyTrackPage(page) is { Title: "Never Gonna Give You Up", Artist: "Rick Astley", Album: "Whenever You Need Somebody", DurationMs: 214000 }
            && ImportReaders.ParseSpotifyTrackPage("<html></html>") is null, "a Spotify song page names the song, artist, album and length");
        Check(ImportReaders.SpotifyTrackIds("https://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC?si=1\nspotify:track:7GhIk7Il098yCjg4BQjzvb\nhttps://open.spotify.com/track/4uLU6hMCjMI75M1A2tKUQC").Count == 2,
            "copied songs are read once each, in order");

        var (ytName, yt) = ImportReaders.ParseYouTube("Road trip\tGet Lucky\tDaft Punk - Topic\t248.0\nRoad trip\tRick Astley - Never Gonna Give You Up (Official Music Video)\tRick Astley\t213\n"
            + "Road trip\t[Private video]\tNA\tNA\nbroken line\nRoad trip\tAlfaaz | Hamza Malik x Zain Zohaib | Official Visualiser\tHamza Malik\t186\n"
            + "Road trip\tKAROL G, Judeline - BbY WOW (Visualizer)\tKAROL G\t226\n");
        Check(ytName == "Road trip" && yt is [{ Title: "Get Lucky", Artist: "Daft Punk", DurationMs: 248000 }, { Title: "Never Gonna Give You Up", Artist: "Rick Astley" },
                { Title: "Alfaaz", Artist: "Hamza Malik" }, { Title: "BbY WOW", Artist: "KAROL G, Judeline" }],
            "YouTube Music songs take the artist from the Topic channel, videos from \"Artist - Title\" or the channel, without extras after \"|\" or video words");
        var (dz, more) = ImportReaders.ParseDeezerTracks("{\"data\":[{\"title\":\"Hey Jude\",\"isrc\":\"gbum71505902\",\"duration\":429,\"artist\":{\"name\":\"The Beatles\"},\"album\":{\"title\":\"1\"}}],\"next\":\"https://api.deezer.com/x\"}");
        Check(dz is [{ Title: "Hey Jude", Artist: "The Beatles", Isrc: "GBUM71505902", DurationMs: 429000 }] && more, "a Deezer playlist page gives songs with their ISRC and whether more follow");
        Reject(() => ImportReaders.ParseDeezerTracks("{\"error\":{\"type\":\"DataException\"}}"), "a private or missing Deezer playlist says so");

        Check(ImportReaders.ParseText("1. Daft Punk - One More Time\nQueen – Bohemian Rhapsody\njust a title\n\n") is [{ Artist: "Daft Punk", Title: "One More Time" }, { Artist: "Queen", Title: "Bohemian Rhapsody" }],
            "a song list reads \"Artist - Title\" lines, with numbering or an en dash, and skips lines without both");
        var exportify = "\"Track URI\",\"Track Name\",\"Artist Name(s)\",\"Album Name\",\"Duration (ms)\",\"ISRC\"\n\"spotify:track:x\",\"Song, With Comma\",\"Artist A,Artist B\",\"Album\",\"215000\",\"usrc17607839\"";
        Check(ImportReaders.ParseCsv(exportify) is [{ Title: "Song, With Comma", Artist: "Artist A,Artist B", Album: "Album", DurationMs: 215000, Isrc: "USRC17607839" }],
            "an Exportify CSV keeps quoted commas, the artist line whole (matching tries its first artist), the length and the ISRC");
        Check(ImportReaders.ParseCsv("Track name;Artist name\nHello, Goodbye;The Beatles\nSeptember;Earth, Wind & Fire") is [{ Title: "Hello, Goodbye", Artist: "The Beatles" }, { Artist: "Earth, Wind & Fire" }]
            && ImportReaders.Detect("Track name;Artist name\nHello, Goodbye;The Beatles").Kind == ImportKind.Csv, "a semicolon CSV keeps commas inside its cells");
        Check(ImportReaders.ParseCsv("﻿Track name;Artist name;Album\nOne;Two;Three") is [{ Title: "One", Artist: "Two", Album: "Three" }]
            && ImportReaders.ParseCsv("Something,Else\n1,2").Count == 0, "a semicolon CSV with a byte order mark works, and a CSV without song columns gives nothing");

        var export = ImportReaders.ParseSpotifyExport("{\"playlists\":[{\"name\":\"Gym\",\"items\":[{\"track\":{\"trackName\":\"Stronger\",\"artistName\":\"Kanye West\",\"albumName\":\"Graduation\"}},{\"episode\":{}}]}],"
            + "\"tracks\":[{\"artist\":\"Queen\",\"album\":\"A Night at the Opera\",\"track\":\"Bohemian Rhapsody\"}]}");
        Check(export is [{ Name: "Gym", Tracks: [{ Title: "Stronger", Artist: "Kanye West" }] }, { Name: "Liked Songs", Tracks: [{ Title: "Bohemian Rhapsody" }] }],
            "Spotify's data export gives each playlist and Liked Songs");
        Reject(() => ImportReaders.ParseSpotifyExport("{\"something\":1}"), "a file that is not a Spotify export says what file to use");
        Reject(() => ImportReaders.ParseSpotifyExport("[{\"endTime\":\"2024-01-01\",\"trackName\":\"x\"}]"), "Spotify's streaming history (an array) is refused with the same message, not an error");

        Check(YtDlp.PlaylistArguments("/deno", "PLabcdefghij12") is var args && args[^1] == "https://www.youtube.com/playlist?list=PLabcdefghij12" && args[^2] == "--"
            && args.Contains("--flat-playlist") && args.Contains("--print") && !args.Contains("--dump-single-json") && !args.Contains("--no-playlist") && args.Contains("--ignore-config") && Throws(() => YtDlp.PlaylistArguments("/deno", "x;rm -rf")) is ProviderException,
            "a YouTube playlist is read flat, from an address built from a checked list id only");

        MusicTrack Song(string title, int ms) => new("1" + ms, title, [new MusicArtistRef("2", "Bonnie Tyler")], "3", null, ms, null, false, null, null);
        var single = new ImportTrack("Total Eclipse of the Heart - Single Edit", "Bonnie Tyler", null, 270_000, null);
        Check(PlaylistImport.Looser(single, [Song("Total Eclipse of the Heart", 420_000), Song("Total Eclipse of the Heart", 300_000)])?.DurationMs == 300_000
            && PlaylistImport.Looser(single, [Song("Total Eclipse of the Heart", 420_000)]) is null && PlaylistImport.Looser(single, [Song("Holding Out for a Hero", 270_000)]) is null,
            "a single edit takes the same song's closest version within a minute, never another song");
        Check(PlaylistImport.Plain("Love of My Life - Remastered 2011") == "Love of My Life" && PlaylistImport.Plain("Stay (feat. Justin Bieber)") == "Stay"
            && PlaylistImport.Plain("(Remix)") == "(Remix)", "a title is searched without its extras, and never searched as nothing");
        MusicTrack Queen(string title, int ms) => new("q" + ms, title, [new MusicArtistRef("4", "Queen")], "5", null, ms, null, false, null, null);
        var legs = new ImportTrack("Death on two Legs", "Queen", null, 223_000, null);
        Check(PlaylistImport.Bare(legs, [Queen("Death On Two Legs (Live, European Tour / 1979)", 300_000), Queen("Death On Two Legs (Dedicated To...)", 223_500)], 3000)?.Title == "Death On Two Legs (Dedicated To...)"
            && PlaylistImport.Bare(legs with { Artist = "Marc Martel" }, [Queen("Death On Two Legs (Dedicated To...)", 223_500)], 3000) is null,
            "a song matches its bracketed catalog title at the same length, and only by the same artist");
        MusicTrack By(string title, string artist, int ms) => new("v" + title.Length + ms, title, [new MusicArtistRef("6", artist)], "7", null, ms, null, false, null, null);
        const string video = "MONEY VERSE RECORDS - KALYANI (with Shreya Ghoshal) MV Money Verse";
        Check(PlaylistImport.InVideo(video, 200_000, [By("Ka", "Shreya Ghoshal", 200_000), By("Kalyani", "Shreya Ghoshal", 205_000), By("Kalyani", "Someone Else", 200_000)])?.Title == "Kalyani"
            && PlaylistImport.InVideo(video, 200_000, [By("Kalyani", "Shreya Ghoshal", 260_000)]) is null
            && PlaylistImport.InVideo(video, null, [By("Kalyan", "Shreya Ghoshal", 200_000)]) is null,
            "a video's song is found when its title and artist both appear as whole words in the video title, at about the same length");
        Check(ImportReaders.ParseYouTube("Chart\tMONEY VERSE RECORDS - KALYANI (with Shreya Ghoshal) OFFICIAL MUSIC VIDEO | ARJN\tMoney Verse\t200\n").Tracks is [{ Video: "MONEY VERSE RECORDS - KALYANI (with Shreya Ghoshal) Money Verse" }],
            "a YouTube video keeps its whole cleaned title and channel for the last search");
        const string atom = """
            <?xml version="1.0" encoding="UTF-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry><updated>2026-11-21T10:00:00Z</updated><link rel="alternate" type="text/html" href="https://github.com/yt-dlp/yt-dlp/releases/tag/2026.11.21"/></entry>
              <entry><updated>2026-11-10T10:00:00Z</updated><link rel="alternate" type="text/html" href="https://github.com/yt-dlp/yt-dlp/releases/tag/2026.11.10"/></entry>
              <entry><updated>2026-11-15T10:00:00Z</updated><link rel="alternate" type="text/html" href="https://github.com/yt-dlp/yt-dlp/releases/tag/2025.12.30"/></entry>
              <entry><updated>2026-11-16T10:00:00Z</updated><link rel="alternate" type="text/html" href="https://github.com/yt-dlp/yt-dlp/releases/tag/v2027.1.0-rc.1"/></entry>
              <entry><updated>2026-11-01T10:00:00Z</updated><link rel="alternate" type="text/html" href="https://github.com/yt-dlp/yt-dlp/releases/tag/bad%20tag%3B"/></entry>
            </feed>
            """;
        Check(YtDlp.SettledTag(atom, new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero) - YtDlp.Settle) == "2026.11.10"
            && YtDlp.SettledTag(atom, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero)) is null
            && Throws(() => YtDlp.SettledTag("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><feed>&e;</feed>", DateTimeOffset.UtcNow)) is System.Xml.XmlException,
            "the YouTube tools install the highest settled release (not a later fix to an older line, not a pre-release), never a malformed tag, and the feed takes no DTD");
        Check(YtDlp.Redact("ERROR: unable to open for writing: [Errno 13] Permission denied: '/var/lib/jellyfin/plugins/x/tmp/a.part' see https://example.com/a/b")
                is var redacted && !redacted.Contains("/var/lib", StringComparison.Ordinal) && redacted.Contains("[path]", StringComparison.Ordinal) && redacted.Contains("https://example.com/a/b", StringComparison.Ordinal),
            "yt-dlp errors shown to apps leave out server paths but keep web addresses");
        var root = Path.Combine(Path.GetTempPath(), "streamfin-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var user = Guid.NewGuid();
            await new MusicLibraryStore(root).UpdateAsync(user, library =>
            {
                library.Playlists.Add(new MusicPlaylist(Guid.NewGuid(), "From Deezer", "", DateTime.UtcNow, DateTime.UtcNow, [], "deezer:908622995"));
                library.Playlists.Add(new MusicPlaylist(Guid.NewGuid(), "Tampered", "", DateTime.UtcNow, DateTime.UtcNow, [], "file:///etc/passwd"));
            }, default);
            var reloaded = await new MusicLibraryStore(root).ReadAsync(user, default);
            Check(reloaded.Playlists is [{ Source: "deezer:908622995" }, { Source: null }], "a playlist keeps where it was imported from, and a source that is not a known kind is dropped");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
