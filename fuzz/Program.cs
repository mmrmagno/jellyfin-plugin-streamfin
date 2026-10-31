using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpFuzz;
using StreamFin;

var targets = new Dictionary<string, Action<string>>(StringComparer.Ordinal)
{
    ["hls_media"] = Targets.HlsMedia,
    ["hls_master"] = Targets.HlsMaster,
    ["hls_text"] = Targets.HlsText,
    ["flix_player"] = Targets.FlixPlayer,
    ["flix_pages"] = Targets.FlixPages,
    ["anime_pages"] = Targets.AnimePages,
    ["hianime_config"] = Targets.HiAnimeConfig,
    ["text"] = Targets.Text,
    ["tmdb"] = Targets.Tmdb,
    ["music"] = Targets.Music,
    ["lyrics"] = Targets.Lyrics,
    ["subsonic"] = Targets.Subsonic,
    ["gain"] = Targets.Gain,
    ["radio"] = Targets.Radio,
    ["ytdlp"] = Targets.YtDlp,
};
if (args.Length == 0 || !targets.TryGetValue(args[0], out var target))
{
    Console.Error.WriteLine("usage: StreamFin.Fuzz <" + string.Join('|', targets.Keys) + "> [inputs to replay]");
    return 2;
}
if (args.Length > 1)
{
    foreach (var file in args[1..]) target(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    Console.WriteLine($"{args[0]}: {args.Length - 1} input(s) passed");
    return 0;
}
Fuzzer.LibFuzzer.Run(data => target(Encoding.UTF8.GetString(data)));
return 0;

static class Targets
{
    private static readonly Uri Playlist = new("https://cdn.example.top/hls/1080/index.m3u8");
    private const string Quoted = "\"[^\"\\\\\\x00-\\x1f]{0,256}\"";
    private const string Plain = "[A-Za-z0-9.\\-]{1,64}";
    private const string Segment = "\\d+\\.(?:m4s|mp4|aac|ts|vtt|webvtt|m4a|m4v|cmfv|cmfa)";
    private static readonly Regex MediaLine = new($@"\A(?:{Segment}|#EXTINF:\d{{1,6}}(?:\.\d{{1,6}})?,"
        + @"|#EXT-X-(?:TARGETDURATION|MEDIA-SEQUENCE|DISCONTINUITY-SEQUENCE|VERSION):\d{1,12}|#EXT-X-PLAYLIST-TYPE:(?:VOD|EVENT)"
        + @"|#EXT-X-BYTERANGE:\d{1,15}(?:@\d{1,15})?|#EXT-X-(?:ENDLIST|DISCONTINUITY|INDEPENDENT-SEGMENTS)"
        + $@"|#EXT-X-KEY:(?:(?:URI=""{Segment}""|(?:METHOD|IV)={Plain})(?:,|\z))*|#EXT-X-MAP:(?:(?:URI=""{Segment}""|BYTERANGE={Quoted})(?:,|\z))*)\z");
    private static readonly Regex MediaTag = new($@"\A#EXT-X-MEDIA:(?:(?:URI=""p/\d+\.m3u8""|(?:GROUP-ID|NAME|LANGUAGE|CHANNELS)={Quoted}|(?:TYPE|DEFAULT|AUTOSELECT)={Plain})(?:,|\z))*\z");
    private static readonly Regex StreamTag = new($@"\A#EXT-X-STREAM-INF:(?:(?:(?:CODECS|AUDIO)={Quoted}|(?:BANDWIDTH|AVERAGE-BANDWIDTH|RESOLUTION|FRAME-RATE)={Plain})(?:,|\z))*\z");
    private static readonly Regex Variant = new(@"\Ap/\d+\.m3u8\z");
    private static readonly Regex VideoId = new(@"\A[A-Za-z0-9_-]{11}\z");
    private static readonly Regex LanguageCode = new(@"\A(?:[a-z]{2}|und)\z");
    private static readonly byte[] HiAnimeKey = "otaku-embed-v1"u8.ToArray();

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Invariant violated: " + message);
    }

    private static bool Expected(Exception ex) => ex is ProviderException or RegexMatchTimeoutException or UriFormatException or JsonException;

    private static void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) when (Expected(ex)) { }
    }

    private static void Open(Uri url, string what)
    {
        Expect(url.IsAbsoluteUri && url.Scheme is "https" or "http" && url.UserInfo.Length == 0, what + " is an absolute web address");
        try { SafeHttp.ValidateOpen(url); }
        catch (ProviderException) { Expect(false, what + " passes the destination check"); }
    }

    private static void Links(HlsLinks links, bool playlist)
    {
        for (var i = 0; links.Get(i) is { } url; i++)
        {
            Open(url, "relayed link");
            Expect(url.OriginalString.Length <= 4096, "relayed link stays under the length cap");
            Expect(links.IsPlaylist(i) == playlist, "relayed link has the expected kind");
        }
    }

    private static void Clean(string text, string what) => Expect(!text.Contains('<') && !text.Contains('>'), what + " carries no markup");

    public static void HlsMedia(string text)
    {
        var links = new HlsLinks();
        string output;
        try { output = Hls.RewriteMedia(text, Playlist, links); }
        catch (Exception ex) when (ex is ProviderException or RegexMatchTimeoutException) { return; }
        var lines = output.Split('\n');
        Expect(lines[0] == "#EXTM3U" && lines[^1].Length == 0, "media playlist framing");
        foreach (var line in lines[1..^1]) Expect(MediaLine.IsMatch(line), "media playlist line is rebuilt from the whitelist: " + line);
        Links(links, playlist: false);
    }

    public static void HlsMaster(string text)
    {
        var links = new HlsLinks();
        string output;
        try { output = Hls.RewriteMaster(text, Playlist, links); }
        catch (Exception ex) when (ex is ProviderException or RegexMatchTimeoutException) { return; }
        var lines = output.Split('\n');
        Expect(lines.Length >= 4 && lines[0] == "#EXTM3U" && lines[^1].Length == 0, "master playlist framing");
        Expect(StreamTag.IsMatch(lines[^3]) && Variant.IsMatch(lines[^2]), "master playlist ends with one rebuilt variant");
        foreach (var line in lines[1..^3]) Expect(MediaTag.IsMatch(line), "master playlist line is a rebuilt rendition: " + line);
        Expect(links.Get(Hls.FirstVariant(output)) is not null, "the variant points at a relayed link");
        Links(links, playlist: true);
    }

    public static void HlsText(string text)
    {
        var attributes = Hls.Attributes(text);
        Expect(attributes.Sum(a => a.Key.Length + a.Value.Length) <= text.Length, "attribute parsing never grows the input");
        Guard(() => Hls.Duration(text));
        try { Expect(Hls.WebVtt(text).StartsWith("WEBVTT", StringComparison.Ordinal), "subtitles come out as WebVTT"); }
        catch (Exception ex) when (ex is ProviderException or RegexMatchTimeoutException) { }
    }

    public static void FlixPlayer(string html)
    {
        Guard(() =>
        {
            foreach (var script in FlixSource.Unpack(html)) Expect(script.Length <= html.Length + 8_000_000, "unpacked script stays under the cap");
        });
        Guard(() => { if (FlixSource.FindMedia(html) is { } media) Open(media, "player media"); });
        Guard(() =>
        {
            foreach (var subtitle in FlixSource.FindSubtitles(html))
            {
                Open(subtitle.Url, "player subtitle");
                Expect(LanguageCode.IsMatch(subtitle.Language) && subtitle.Label.Length > 0, "player subtitle has a label and language");
            }
        });
    }

    public static void FlixPages(string text)
    {
        Guard(() =>
        {
            foreach (var server in FlixSource.ParseListing(text))
                Expect(server.Link.IsAbsoluteUri && server.Link.Scheme is "https" or "http" && server.Name.Length > 0, "listed server has a web address and a name");
        });
        Guard(() =>
        {
            foreach (var server in FlixSource.ParsePlayers(text))
                Expect(server.Link.IsAbsoluteUri && server.Link.Scheme is "https" or "http", "player has a web address");
        });
        Guard(() =>
        {
            foreach (var subtitle in FlixSource.ParseSubtitleIndex(text))
            {
                Open(subtitle.Url, "indexed subtitle");
                Expect(subtitle.Label.Length > 0 && !subtitle.Label.Contains("thumbnail", StringComparison.OrdinalIgnoreCase), "indexed subtitle is a real track");
            }
        });
        Guard(() => Expect(Regex.IsMatch(FlixSource.Slug(text), @"\A(?:[a-z0-9]+(?:-[a-z0-9]+)*)?\z"), "slug is lowercase words joined by dashes"));
        foreach (var film in new[] { new Film(1, FilmKind.Movie, "The Example", 2020, "", null, null, false), new Film(2, FilmKind.Tv, "Example Show", null, "", null, null, false) })
        {
            Guard(() => { if (FlixSource.MatchWatch(text, film) is { } page) Expect(page.Host == "flixhqz.net", "watch page stays on its site"); });
            Guard(() => { if (FlixSource.MatchClassic(text, film) is { } page) Expect(page.Host == "flixhq.ws", "classic page stays on its site"); });
        }
    }

    public static void AnimePages(string html)
    {
        Guard(() => { foreach (var title in AnimeGgSource.ParseSearch(html)) Clean(title.Title, "AnimeGG title"); });
        Guard(() => { foreach (var episode in AnimeGgSource.ParseEpisodes(html)) Clean(episode.Title, "AnimeGG episode"); });
        Guard(() => Expect(Regex.IsMatch(AnimeGgSource.ParseSubEmbed(html), @"\A/embed/[0-9]+\z", RegexOptions.IgnoreCase), "AnimeGG embed is a local path"));
        Guard(() =>
        {
            foreach (var video in AnimeGgSource.ParseVideos(html))
            {
                try { SafeHttp.Validate(video.Url, true); }
                catch (ProviderException) { Expect(false, "AnimeGG video stays on an allowed host"); }
            }
        });
        Guard(() =>
        {
            foreach (var title in AnimeHeavenSource.ParseSearch(html))
            {
                Clean(title.Title, "AnimeHeaven title");
                Expect(Regex.IsMatch(title.Id, @"\A[a-z0-9]{1,40}\z", RegexOptions.IgnoreCase), "AnimeHeaven title id is validated");
            }
        });
        Guard(() =>
        {
            foreach (var episode in AnimeHeavenSource.ParseEpisodes(html))
                Expect(Regex.IsMatch(episode.Id, @"\A[a-f0-9]{32}\z", RegexOptions.IgnoreCase) && episode.Number <= int.MaxValue, "AnimeHeaven episode is validated");
        });
        Guard(() =>
        {
            foreach (var video in AnimeHeavenSource.ParseVideos(html))
                Expect(AnimeHeavenSource.IsMediaHost(video.Url.IdnHost) && video.Url.AbsolutePath == "/video.mp4" && video.Url.Scheme == "https", "AnimeHeaven video stays on its media hosts");
        });
        Guard(() =>
        {
            var (_, _, body) = AnimeHeavenPages.ParseResponse(html);
            Expect(html.EndsWith(body, StringComparison.Ordinal), "response body is the tail of the response");
        });
        HiAnime(html);
        Guard(() =>
        {
            if (HiAnimeSource.ParseKey(html) is { } key) Expect(key.Length is >= 4 and <= 64 && key.All(b => b is >= 0x21 and <= 0x7e), "HiAnime key is short printable ASCII");
        });
    }

    public static void HiAnimeConfig(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (var i = 0; i < bytes.Length; i++) bytes[i] ^= HiAnimeKey[i % HiAnimeKey.Length];
        HiAnime("<script>window.__P = \"" + Convert.ToBase64String(bytes) + "\";</script>");
    }

    private static void HiAnime(string html) => Guard(() =>
    {
        var video = HiAnimeSource.ParseEmbed(html).Single();
        Open(video.Url, "HiAnime stream");
        Expect(video.Url.Scheme == "https" && video.Hls, "HiAnime stream is HTTPS HLS");
        var subtitles = video.Subtitles ?? [];
        Expect(subtitles.Count is > 0 and <= 8 && subtitles[0].Language == "en", "HiAnime keeps English first and at most eight tracks");
        foreach (var subtitle in subtitles)
        {
            Open(subtitle.Url, "HiAnime subtitle");
            Expect(subtitle.Url.Scheme == "https" && LanguageCode.IsMatch(subtitle.Language), "HiAnime subtitle is HTTPS with a language");
        }
    });

    public static void Text(string text)
    {
        Guard(() => Clean(AnimeGgSource.Clean(text), "cleaned text"));
        Guard(() => Expect(Regex.IsMatch(AnimeGgSource.Normalize(text), @"\A[\p{L}\p{N}]*\z"), "normalized title is letters and numbers"));
        Guard(() => Expect(LanguageCode.IsMatch(Languages.Code(text, text)) && LanguageCode.IsMatch(Languages.Code("", text)), "language code is two letters or und"));
        Guard(() =>
        {
            var part = MusicPaths.Part(text);
            Expect(part.Length is > 0 and <= 80 && Encoding.UTF8.GetByteCount(part) <= 180 && part is not ("." or "..")
                && part.IndexOfAny(['/', '\\', '\0']) < 0 && part == part.Trim(), "music path part is one short segment");
            Expect(MusicPaths.Album("/srv/music", new MusicRelease("155102851176075264", text, [new("155102851176075265", text)], "ALBUM", null, null, null, null, []))
                .StartsWith("/srv/music/", StringComparison.Ordinal), "saved album stays inside the library folder");
        });
    }

    public static void Tmdb(string json)
    {
        Guard(() =>
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var kind in new FilmKind?[] { null, FilmKind.Movie, FilmKind.Tv })
            {
                if (TmdbCatalog.Parse(document.RootElement, kind) is not { } film) continue;
                Clean(film.Title, "TMDB title");
                Clean(film.Overview, "TMDB overview");
                Expect(film.Title.Length > 0, "TMDB title is not empty");
                Expect(film.Image is null || film.Image.StartsWith("https://image.tmdb.org/t/p/w500/", StringComparison.Ordinal), "TMDB poster stays on the TMDB image host");
            }
        });
    }

    private static void Track(MusicTrack track)
    {
        Clean(track.Title, "music title");
        Expect(MusicCatalog.IsId(track.Id) && MusicCatalog.IsId(track.ReleaseId), "music track IDs are numeric");
        Expect(track.Artists.All(a => MusicCatalog.IsId(a.Id) && !a.Name.Contains('<') && !a.Name.Contains('>')), "music artists are clean");
        Expect(track.Artwork is null || track.Artwork.StartsWith("https://tracks.monochrome.st/proxy/", StringComparison.Ordinal), "music artwork stays on the catalog host");
        Expect(MusicCatalog.StreamUri(track.Id).IdnHost == "tracks.monochrome.st", "music stream stays on the catalog host");
    }

    private static void Release(MusicRelease release)
    {
        Clean(release.Title, "music release title");
        Expect(MusicCatalog.IsId(release.Id), "music release ID is numeric");
        Expect(release.Tracks.All(t => t.ReleaseId == release.Id) || release.Tracks.Count == 0, "release tracks belong to the release");
        foreach (var track in release.Tracks) Track(track);
    }

    public static void Music(string json)
    {
        Guard(() =>
        {
            var results = MusicCatalog.ParseSearch(json);
            foreach (var track in results.Tracks) Track(track);
            var kept = MusicCatalog.WithoutCleanCopies(results.Tracks);
            Expect(kept.All(results.Tracks.Contains) && results.Tracks.Where(t => t.Explicit).All(t => kept.Any(k => (k.Isrc ?? k.Id) == (t.Isrc ?? t.Id))),
                "only clean copies and repeats of a recording are dropped");
            foreach (var release in results.Releases) Release(release);
        });
        Guard(() => Release(MusicCatalog.ParseRelease(json)));
        Guard(() =>
        {
            var artist = MusicCatalog.ParseArtist(json);
            Clean(artist.Name, "music artist name");
            Clean(artist.Bio, "music artist biography");
            foreach (var track in artist.TopTracks) Track(track);
            foreach (var release in artist.Releases) Release(release);
        });
    }

    // Catalog text ends up in Subsonic responses; whatever it holds, both encodings must stay well formed.
    public static void Subsonic(string text)
    {
        var node = SubsonicResponse.Root(true).One(new SubsonicNode("album").Set("name", text).Set("isrc", new[] { text, "x" })
            .Add(new SubsonicNode("song").Set("title", text)).Add(new SubsonicNode("line") { Text = text }));
        var json = JsonDocument.Parse(SubsonicResponse.Json(node)).RootElement.GetProperty("subsonic-response").GetProperty("album");
        // Empty values are left out of both encodings.
        Expect(text.Length == 0 ? !json.TryGetProperty("name", out _) : json.GetProperty("name").GetString() == text, "Subsonic JSON keeps text exactly");
        var xml = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(SubsonicResponse.Xml(node)));
        var name = xml.Root?.Elements().Single().Attribute("name")?.Value;
        Expect((text.Length == 0 || name is not null) && !(name ?? "").Any(c => c < 0x20 && c is not ('\t' or '\n' or '\r')),
            "Subsonic XML parses and carries no control characters");
    }

    private static readonly MusicRelease Album = new("155102851176075264", "Album", [new("155102851176075265", "Artist")], "ALBUM", new DateTime(2020, 1, 1), "Label", null, 1, []);
    private static readonly MusicTrack Song = new("155102871061270528", "Song", [new("155102851176075265", "Artist")], "155102851176075264", null, 200000, null, true, 1, 1);

    // FLAC is binary; Latin-1 maps each char back to one byte.
    public static void Gain(string text)
    {
        Guard(() =>
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            Expect(MusicGain.AudioStart(bytes) is not int audio || audio >= 8 && audio <= bytes.Length, "audio starts inside the bytes read");
            if (MusicTags.Retag(bytes, MusicTags.For(Album, Song)) is { } header)
            {
                var info = 3 + ((bytes[5] << 16) | (bytes[6] << 8) | bytes[7]);
                Expect(MusicGain.AudioStart(header) == header.Length && (header[4] & 0x7f) == 0 && header.AsSpan(5, info).SequenceEqual(bytes.AsSpan(5, info)),
                    "a retagged header is a whole FLAC header that keeps the stream info first");
                Expect(MusicGain.Parse(header) is not { AlbumGain: not null } || MusicGain.Parse(bytes) is { AlbumGain: not null }, "retagging never invents an album gain");
            }
            if (MusicGain.Parse(bytes) is not { } gain) return;
            Expect(gain.TrackGain is not null || gain.AlbumGain is not null, "a gain result carries a gain");
            Expect(new[] { gain.TrackGain, gain.AlbumGain }.All(g => g is null or >= -64 and <= 64)
                && new[] { gain.TrackPeak, gain.AlbumPeak }.All(g => g is null or >= 0 and <= 16), "gains and peaks are in range");
        });
    }

    private static readonly Regex Uuid = new(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z");

    // MusicBrainz, ListenBrainz and Last.fm answers; names reach channel listings and Subsonic responses.
    public static void Radio(string json)
    {
        Guard(() => Expect(MusicRadio.ParseIsrc(json, "Radiohead") is not { } id || Uuid.IsMatch(id), "ISRC lookup yields a UUID"));
        Guard(() => Expect(MusicRadio.ParseArtistSearch(json, "Radiohead") is not { } id || Uuid.IsMatch(id), "artist search yields a UUID"));
        foreach (var parse in new Func<string, IReadOnlyList<SimilarArtist>>[] { j => MusicRadio.ParseSimilar(j), MusicRadio.ParseLastFm })
            Guard(() =>
            {
                var artists = parse(json);
                Expect(artists.Count <= 50 && artists.All(a => a.Name.Length is > 0 and <= 300 && double.IsFinite(a.Score) && a.Score >= 0), "similar artists are bounded");
                foreach (var a in artists) Clean(a.Name, "similar artist name");
                Expect(artists.Zip(artists.Skip(1)).All(p => p.First.Score >= p.Second.Score), "similar artists are best first");
            });
    }

    public static void YtDlp(string json)
    {
        Guard(() =>
        {
            var hits = MusicAudio.ParseSearch(json);
            Expect(hits.Count <= 20 && hits.All(h => VideoId.IsMatch(h.Id)), "YouTube results are bounded with valid video IDs");
            foreach (var hit in hits) StreamFin.YtDlp.AudioArguments("/d", hit.Id);
            foreach (var track in new[] { Song, Song with { Title = json.Length > 200 ? json[..200] : json, DurationMs = 225000 } })
                if (MusicAudio.Best(hits, track) is { } best)
                    Expect(hits.Contains(best) && best.Duration is double d && Math.Abs(d - track.DurationMs!.Value / 1000.0) <= 3, "a chosen YouTube copy has the song's length");
            Expect(MusicAudio.Normalize(json).Length <= json.Length * 4 + 8, "normalised names stay bounded");
        });
    }

    public static void Lyrics(string json)
    {
        Guard(() =>
        {
            foreach (var duration in new int?[] { 200000, null })
            {
                if (MusicLyrics.Parse(json, Song with { DurationMs = duration }, "Album") is not { } lyrics) continue;
                Expect(lyrics.Lyrics.Count is > 0 and <= 2000, "lyrics have a bounded number of lines");
                Expect(lyrics.Lyrics.All(l => l.Text.Length <= 500 && !l.Text.Contains('<') && !l.Text.Contains('>')), "lyric lines are short plain text");
                Expect(lyrics.Metadata.IsSynced == true ? lyrics.Lyrics.All(l => l.Start >= 0) && lyrics.Lyrics.Zip(lyrics.Lyrics.Skip(1)).All(p => p.First.Start <= p.Second.Start)
                    : lyrics.Lyrics.All(l => l.Start is null), "synced lyrics are timed in order, plain lyrics are untimed");
            }
        });
    }
}
