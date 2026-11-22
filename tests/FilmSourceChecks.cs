using StreamFin;
using static Harness;

// Movies & TV: VidRift and Rive, ranking versions by quality, and the relay's choice of variant.
static class FilmSourceChecks
{
    public static void Run()
    {
        // Keys the site itself sent for these ids (checked against rivestream.app, which refuses a wrong one).
        Check(RiveSource.Key(27205) == "LTZmMzMzMjM5" && RiveSource.Key(1396) == "Nzg3ZmU5YTI=" && RiveSource.Key(693134) == "MzgzM2RmMDc=",
            "Rive's request key matches the one its site computes");
        var movie = new Film(27205, FilmKind.Movie, "Inception", 2010, "", null, 148, false);
        var show = new Film(1396, FilmKind.Tv, "Breaking Bad", 2008, "", null, 47, false);
        var at = DateTimeOffset.FromUnixTimeMilliseconds(1_795_200_000_000);
        Check(RiveSource.Query(show, 1, 2, "citadel", at) == "?requestID=tvVideoProvider&id=1396&season=1&episode=2&service=citadel&cb=598400&secretKey=Nzg3ZmU5YTI%3D&proxyMode=noProxy",
            "Rive asks for an episode with the site's key and time bucket");
        var rive = RiveSource.Parse("""{"data":{"sources":[{"quality":"480p | English","url":"https://cdn.example/480.m3u8","format":"hls"},{"quality":"720p | Tamil","url":"https://cdn.example/ta.m3u8","format":"hls"},{"quality":"720p | English","url":"https://cdn.example/720.m3u8","format":"hls"}],"captions":[{"file":"https://cdn.example/en.vtt","language":"English"},{"file":"https://cdn.example/fr.vtt","language":"French"}]}}""", englishOnly: true);
        Check(rive is { Quality: "720p" } && rive.Value.Stream.AbsolutePath == "/720.m3u8" && rive.Value.Subtitles.Count == 1,
            "Rive takes the tallest English stream and English captions");
        Check(RiveSource.Parse("""{"data":{"sources":[{"quality":"720p | Hindi","url":"https://cdn.example/hi.m3u8","format":"hls"}]}}""", englishOnly: true) is null,
            "Rive's Citadel stream without English audio is skipped");

        Check(VidRiftSource.BootPath(show, 1, 1, "a b") == "api/boot/tv/1396/1/1?type=tv&id=1396&season=1&episode=1&source=0&provider=&shell=1&token=a%20b"
            && VidRiftSource.BootPath(movie, null, null, null) == "api/boot/movie/27205?type=movie&id=27205&source=0&provider=&shell=1"
            && VidRiftSource.TokenBody(show, 2, 3) == "{\"tmdbId\":1396,\"type\":\"tv\",\"season\":2,\"episode\":3}", "VidRift asks for a token and a boot answer per title");
        var boot = """{"meta":{"bigbangUrl":"https://bb.example/index.m3u8.png?sig=1","bigbangCodec":"h264","bigbangSubtitles":[{"lang":"en","label":"English · BigBang","url":"https://bb.example/en.vtt.png"},{"lang":"ar","url":"https://bb.example/ar.vtt"}],"orionStreams":[{"name":"Orion · Original","url":"/api/orion/x/master.m3u8"}]},"shell":{"subtitles":[{"lang":"en","label":"English","url":"/api/subtitles/movie/27205/en"},{"lang":"en","url":"https://evil.example/x.vtt"}]}}""";
        var parsed = VidRiftSource.Parse(boot);
        Check(parsed is { Quality: "HLS H.264" } && parsed.Value.Stream.Host == "bb.example" && parsed.Value.Headers.Count == 0 && parsed.Value.Subtitles.Count == 2
            && parsed.Value.Subtitles.All(s => s.Url.Host is "bb.example" or "embed.vidrift.net"), "VidRift prefers BigBang, keeps English subtitles from its own hosts only");
        var orion = VidRiftSource.Parse("""{"meta":{"bigbangUrl":"","orionStreams":[{"name":"Orion · Hindi","url":"/api/orion/h/master.m3u8"},{"name":"Orion · Original","url":"/api/orion/o/master.m3u8"}]}}""");
        Check(orion is { Quality: "HLS HEVC" } && orion.Value.Stream.AbsoluteUri == "https://embed.vidrift.net/api/orion/o/master.m3u8" && orion.Value.Headers["Referer"] == "https://embed.vidrift.net/",
            "without a token VidRift's original-language HEVC stream plays, with its referrer");
        Check(VidRiftSource.Parse("""{"meta":{"bigbangUrl":"","orionStreams":[]}}""") is null && VidRiftSource.ParseToken("""{"token":"abc"}""") == "abc"
            && VidRiftSource.ParseToken("""{"error":"x"}""") is null, "a title VidRift lacks is not listed; its token is read when given");

        Check(FilmSources.LabelHeight("720p | English") == 720 && FilmSources.LabelHeight("HLS 1080p") == 1080 && FilmSources.LabelHeight("4K") == 2160 && FilmSources.LabelHeight("HLS") == 0,
            "a quality label gives the picture height when it names one");
        var sources = FilmSources.Order.ToDictionary(id => id, id => (IFilmSource)new Named(id));
        FilmVersion V(string id, int height, bool? h264) => new(sources[id], [new ResolvedVideo(new Uri("https://x.example/" + id), "HLS", new Dictionary<string, string>(), false, true)], height, h264, TimeSpan.Zero);
        var found = new[] { V("flix", 0, null), V("vidly", 1080, false), V("vidrift", 2160, true), V("astra", 1080, true), V("rive", 720, null) };
        Check(FilmSources.Rank(found, null, true).Select(v => v.Source.Id).SequenceEqual(["vidrift", "astra", "vidly", "rive", "flix"]),
            "best quality first, H.264 before HEVC at the same height, an unknown height last");
        Check(FilmSources.Rank(found, 1080, true).Select(v => v.Source.Id).SequenceEqual(["astra", "vidly", "rive", "flix", "vidrift"]),
            "a 1080p cap puts the 4K version last, not first");
        Check(FilmSources.Rank(found, null, false).Select(v => v.Source.Id).Take(3).SequenceEqual(["vidrift", "astra", "vidly"]),
            "without the H.264 preference equal heights keep the usual order");
        Check(V("vidrift", 2160, true).Label == "VidRift · 4K · H.264" && V("vidly", 1080, false).Label == "Vidly · 1080p · HEVC" && V("flix", 0, null).Label == "FlixHQ · HLS"
            && FilmVersion.Name("Punkflix", 3840, 1600, true, 1) == "Punkflix · 4K · H.264 · 1 subtitle track" && FilmVersion.Name("Astra", 1422, 800, true, 6) == "Astra · 720p · H.264 · 6 subtitle tracks",
            "versions are named by source, quality and codec, a wide film by its width");
        Check(FilmVersions.VersionId("movie/popular/27205", "astra") == FilmVersions.VersionId("movie/popular/27205", "astra")
            && FilmVersions.VersionId("movie/popular/27205", "astra") != FilmVersions.VersionId("movie/popular/27205", "vidly"), "a version keeps its id between the page and the player");
        Check(FilmVersions.Parse("movie/popular/27205") == ("movie", 27205, null, null) && FilmVersions.Parse("ep/tv/1396/1/2") == ("ep", 1396, 1, 2)
            && FilmVersions.Parse("episode/popular/1/x") is null && FilmVersions.Parse("ep/tv/1396/0/2") is null && FilmVersions.Parse(null) is null,
            "only Movies & TV titles get versions; anime episodes and bad ids do not");

        var dubbed = new MediaBrowser.Model.Dto.MediaSourceInfo { MediaStreams = [
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 0 },
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 1, Language = "hin", IsDefault = true },
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 2, Language = "eng" }] };
        FilmVersions.PreferEnglish(dubbed);
        Check(dubbed.DefaultAudioStreamIndex == 2 && dubbed.MediaStreams[2].IsDefault && !dubbed.MediaStreams[1].IsDefault, "a version with an English dub plays English by default");
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];
        var ts = new byte[400]; ts[0] = 0x47; ts[188] = 0x47;
        Check(Hls.ImagePrefix([.. png, .. ts]) == png.Length && Hls.ImagePrefix(ts) == 0 && Hls.ImagePrefix([.. png, 1, 2, 3, 4, 5, 6, 7, 8, 9]) == 0
            && Hls.ImagePrefix([.. png, 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p']) == png.Length,
            "an image glued in front of a segment is cut off only when video follows it");
        Check(Hls.Extension(new Uri("https://h.example/seg-1.m4s.png?sig=1")) == "m4s" && Hls.Extension(new Uri("https://h.example/0.jpg")) == "ts"
            && Hls.Extension(new Uri("https://h.example/0.jpg"), fragmented: true) == "m4s" && Hls.Extension(new Uri("https://h.example/a.ts")) == "ts",
            "segments disguised as images keep their real media extension");
        var master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=9000000,RESOLUTION=3840x2160,CODECS=\"avc1.640033,mp4a.40.2\"\nu4k.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=6000000,RESOLUTION=1920x1080,CODECS=\"hvc1.1.6.L120,mp4a.40.2\"\nhevc.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS=\"avc1.640028,mp4a.40.2\"\navc.m3u8\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720,CODECS=\"avc1.64001f,mp4a.40.2\"\n720.m3u8\n";
        Check(Hls.Best(master) is { Height: 2160, H264: true } && Hls.Best(master, 1080, true) is { Height: 1080, H264: true, Bandwidth: 5000000 }
            && Hls.Best(master, 1080, false) is { Height: 1080, Bandwidth: 6000000 } && Hls.Best(master, 480, true) is { Height: 720 },
            "the relay plays the tallest variant within the cap, H.264 first when asked, the smallest when all are too tall");
        Check(Hls.Best("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=4629855,CODECS=\"avc1.640028\",RESOLUTION=1920x800\nm.m3u8\n") is { Height: 1080 },
            "a wide 1920x800 film counts as 1080p");
        var links = new HlsLinks();
        var rewritten = Hls.RewriteMaster(master, new Uri("https://h.example/master.m3u8"), links, 1080, true);
        Check(rewritten.Contains("RESOLUTION=1920x1080", StringComparison.Ordinal) && links.Get(0)?.AbsolutePath == "/avc.m3u8",
            "the rewritten playlist keeps only the chosen variant");
    }

    private sealed class Named(string id) : IFilmSource
    {
        public string Id => id;
        public string Name => VideoHealth.Label(id);
        public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct) => throw new NotSupportedException();
    }
}
