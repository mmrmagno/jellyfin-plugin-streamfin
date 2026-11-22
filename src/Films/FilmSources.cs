using System.Globalization;
using System.Security.Cryptography;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamFin;

// A site that turns a TMDB movie or episode into a playable video.
public interface IFilmSource
{
    string Id { get; }
    string Name { get; }
    Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct);
}

// One source's answer for a title, with what its playlist says about the picture.
public sealed record FilmVersion(IFilmSource Source, IReadOnlyList<ResolvedVideo> Videos, int Height, bool? H264, TimeSpan Took)
{
    public string Label => Name(Source.Name, 0, Height, H264, Videos[0].Subtitles?.Count ?? 0, Videos[0].Quality);

    // "VidRift · 4K · H.264 · 4 subtitle tracks". The width decides when it is known, so a wide 3840x1600 film is 4K.
    public static string Name(string source, int width, int height, bool? h264, int subtitles, string fallback = "HLS")
    {
        var quality = width >= 3200 || height >= 2000 ? "4K" : width >= 1800 ? "1080p" : width >= 1200 ? "720p"
            : height > 0 ? height.ToString(CultureInfo.InvariantCulture) + "p" : fallback.Split(' ')[0];
        var codec = h264 switch { true => " · H.264", false => " · HEVC", _ => "" };
        return $"{source} · {quality}{codec}" + (subtitles == 1 ? " · 1 subtitle track" : subtitles > 1 ? $" · {subtitles} subtitle tracks" : "");
    }
}

// Movies & TV sources. Every enabled site is asked at once; each answer's playlist says how tall the picture is and
// whether it is H.264, and the best one under the quality settings plays while the others are offered as versions. A
// site that is down is skipped, and the one that worked is asked again when its link expires.
public sealed class FilmSources(IEnumerable<IFilmSource> sources, VideoHealth health, SafeHttp http)
{
    public static readonly string[] Order = ["flix", "astra", "vidrift", "vidly", "vidbolt", "rive", "punkflix"];
    public static bool IsEnabled(string id) => id switch
    {
        "vidly" => Plugin.Instance?.Configuration.FilmVidly != false,
        "astra" => Plugin.Instance?.Configuration.FilmAstra != false,
        "vidbolt" => Plugin.Instance?.Configuration.FilmVidBolt != false,
        "punkflix" => Plugin.Instance?.Configuration.FilmPunkflix != false,
        "vidrift" => Plugin.Instance?.Configuration.FilmVidRift != false,
        "rive" => Plugin.Instance?.Configuration.FilmRive != false,
        _ => true
    };
    public static int? MaxHeight => Plugin.Instance?.Configuration.FilmMaxHeight is > 0 and var cap ? cap : null;
    public static bool PreferH264 => Plugin.Instance?.Configuration.FilmPreferH264 != false;

    public IReadOnlyList<IFilmSource> Enabled => sources.Where(s => IsEnabled(s.Id))
        .OrderBy(s => health.Usable(s.Id) ? 0 : 1).ThenBy(s => Array.IndexOf(Order, s.Id) is var i and >= 0 ? i : Order.Length).ToArray();

    // Best first: within the height cap, then taller, then H.264 when preferred, then the usual order (FlixHQ first).
    public static IReadOnlyList<FilmVersion> Rank(IEnumerable<FilmVersion> versions, int? maxHeight, bool preferH264) => versions
        .OrderBy(v => maxHeight is { } cap && v.Height > cap ? 1 : 0)
        .ThenByDescending(v => v.Height)
        .ThenByDescending(v => preferH264 && v.H264 == true)
        .ThenBy(v => Array.IndexOf(Order, v.Source.Id) is var i and >= 0 ? i : Order.Length)
        .ToArray();

    // The picture a source's answer offers: a master playlist names its variants; otherwise its own quality label
    // ("720p") is all there is. Never fails: an answer that cannot be described still plays, ranked by its label.
    public async Task<(int Height, bool? H264)> DescribeAsync(ResolvedVideo video, CancellationToken ct)
    {
        var labelled = LabelHeight(video.Quality);
        // A source that knows its codec says so in its label when the playlist does not.
        bool? named = video.Quality.Contains("HEVC", StringComparison.OrdinalIgnoreCase) ? false
            : video.Quality.Contains("H.264", StringComparison.OrdinalIgnoreCase) ? true : null;
        if (!video.Hls) return (labelled, named);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            var text = await http.TextAsync(video.Url, limit.Token, headers: video.Headers, open: true);
            if (Hls.IsMaster(text) && Hls.Best(text, MaxHeight, PreferH264) is { } best)
                return (best.Height > 0 ? best.Height : labelled, best.Codecs is null ? named : best.H264);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
        return (labelled, named);
    }

    public static int LabelHeight(string? quality) => quality is not null
        && Regex.Match(quality, @"(\d{3,4})p", RegexOptions.None, TimeSpan.FromSeconds(1)) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h
        : quality?.Contains("4K", StringComparison.OrdinalIgnoreCase) == true ? 2160 : 0;

    // Every enabled site at once. Once one has answered the others get a few more seconds, so a slow or hanging site
    // never holds up playback; the answers are ranked best first.
    public async Task<IReadOnlyList<FilmVersion>> ResolveAllAsync(Film film, int? season, int? episode, CancellationToken ct, TimeSpan? grace = null)
    {
        var enabled = Enabled;
        if (enabled.Count == 0) throw new ProviderException("Every Movies & TV source is switched off in the settings.");
        using var all = CancellationTokenSource.CreateLinkedTokenSource(ct);
        all.CancelAfter(TimeSpan.FromSeconds(40));
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var tasks = enabled.Select(source => AskAsync(source, film, season, episode, errors, all.Token)).ToList();
        var found = new List<FilmVersion>();
        var pending = new List<Task>(tasks);
        using var after = CancellationTokenSource.CreateLinkedTokenSource(all.Token);
        var waited = Task.Delay(Timeout.Infinite, after.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Append(waited));
                if (done == waited) break;
                pending.Remove(done);
                if (await (Task<FilmVersion?>)done is { } version)
                {
                    if (found.Count == 0) after.CancelAfter(grace ?? TimeSpan.FromSeconds(2));
                    found.Add(version);
                }
            }
        }
        // Sites still answering after the grace time are stopped; a late answer would not be used.
        finally { after.Cancel(); all.Cancel(); }
        ct.ThrowIfCancellationRequested();
        if (found.Count > 0) return Rank(found, MaxHeight, PreferH264);
        var order = enabled.Select(s => s.Name).ToList();
        var reasons = errors.OrderBy(e => order.IndexOf(e.Key)).Select(e => e.Key + ": " + e.Value).ToList();
        throw new ProviderException("No source could play this title right now (" + (reasons.Count > 0 ? string.Join("; ", reasons) : "no source answered in time") + "). Please try again later.");
    }

    private async Task<FilmVersion?> AskAsync(IFilmSource source, Film film, int? season, int? episode,
        System.Collections.Concurrent.ConcurrentDictionary<string, string> errors, CancellationToken ct)
    {
        await Task.Yield();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var videos = await source.ResolveAsync(film, season, episode, ct);
            if (videos.Count == 0) { errors[source.Name] = "no video"; return null; }
            var (height, h264) = await DescribeAsync(videos[0], ct);
            return new FilmVersion(source, videos, height, h264, clock.Elapsed);
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or JsonException or UriFormatException
            or RegexMatchTimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            health.Failed(source.Id, ex, ct);
            errors[source.Name] = ex is SourceStatusException { Status: 404 } ? "not listed"
                : ex is SourceStatusException status ? $"HTTP {status.Status}" : ex is ProviderException ? ex.Message.TrimEnd('.') : "unavailable";
            return null;
        }
        catch (OperationCanceledException) { errors.TryAdd(source.Name, "too slow"); return null; }
    }

    // One site, asked again for a fresh link to the version that is playing.
    public IFilmSource? Get(string id) => sources.FirstOrDefault(s => s.Id == id);
}

// vidly.pro, keyed by TMDB id, so no title matching: the page carries the stream as a preload link, and a separate
// endpoint lists subtitle tracks.
public sealed class VidlySource(SafeHttp http) : IFilmSource
{
    public static readonly Uri Origin = new("https://vidly.pro/");
    public string Id => "vidly";
    public string Name => "Vidly";
    private static readonly Regex PreloadTag = new(@"<link\b[^>]*\bpreload\b[^>]*>", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex Href = new(@"\bhref\s*=\s*""(https://[^""]+)""", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public static string Path(Film film, int? season, int? episode) => film.Kind == FilmKind.Movie
        ? $"movie/{film.Id.ToString(CultureInfo.InvariantCulture)}"
        : $"tv/{film.Id.ToString(CultureInfo.InvariantCulture)}/{season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}/{episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}";

    // The first preloaded HLS playlist on the page. A title the site does not have renders a page without one.
    public static Uri? StreamFrom(string html) => PreloadTag.Matches(html).Select(tag => Href.Match(tag.Value))
        .Where(m => m.Success).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
        .Where(link => link.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
        .Select(link => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null).FirstOrDefault(uri => uri is not null);

    // English tracks only, as on the other sources; the rest are dropped.
    public static IReadOnlyList<Subtitle> SubtitlesFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("subtitles", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var found = new List<Subtitle>();
        foreach (var item in list.EnumerateArray())
        {
            string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (Text("lang") is not "en" || Text("url") is not { } link || !Uri.TryCreate(link, UriKind.Absolute, out var url) || url.Scheme != "https") continue;
            found.Add(new Subtitle(Text("label") ?? "English", "en", url));
        }
        return Languages.Arrange(found);
    }

    // The same English tracks serve Astra and VidBolt, which carry none of their own. Missing subtitles never stop playback.
    public static async Task<IReadOnlyList<Subtitle>> SubtitlesAsync(SafeHttp http, Film film, int? season, int? episode, CancellationToken ct)
    {
        try
        {
            var query = film.Kind == FilmKind.Movie ? $"api/subtitles?kind=movie&tmdb={film.Id}" : $"api/subtitles?kind=tv&tmdb={film.Id}&s={season}&e={episode}";
            return SubtitlesFrom(await http.TextAsync(new Uri(Origin, query), ct, open: true));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ProviderException or TaskCanceledException && !ct.IsCancellationRequested) { return []; }
    }

    // A stream counts only if it really answers with an HLS playlist.
    public static async Task EnsurePlaylistAsync(SafeHttp http, Uri stream, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!(await http.TextAsync(stream, ct, headers: headers, open: true)).TrimStart('\uFEFF', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
            throw new ProviderException("the player did not return an HLS playlist");
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var page = new Uri(Origin, Path(film, season, episode));
        var html = await http.TextAsync(page, ct, open: true);
        if (StreamFrom(html) is not { } stream) throw new ProviderException(film.Kind == FilmKind.Movie ? "not listed" : $"season {season} episode {episode} is not listed");
        var headers = new Dictionary<string, string> { ["Referer"] = Origin.ToString(), ["Origin"] = Origin.GetLeftPart(UriPartial.Authority) };
        await EnsurePlaylistAsync(http, stream, headers, ct);
        return [new ResolvedVideo(stream, "HLS", headers, false, true, await SubtitlesAsync(http, film, season, episode, ct))];
    }
}

// Astra, the player behind fprime.at: a JSON answer whose stream address is AES-256-GCM encrypted with a fixed key from
// fprime's player (hex after "as_": 12-byte nonce, ciphertext, 16-byte tag). H.264, so most clients play it without a
// transcode. If fprime changes the key, decryption fails and the next source is tried.
public sealed class AstraSource(SafeHttp http) : IFilmSource
{
    private static readonly Uri Api = new("https://serverone.insidethisplaceiswarm.cc/astra");
    private static readonly byte[] Key = Convert.FromHexString("cc668a8304b57864709efdfc0051a619e146c0c8b486f190a06d1c1cf96af698");
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["Referer"] = "https://fprime.at/", ["Origin"] = "https://fprime.at" };
    public string Id => "astra";
    public string Name => "Astra";

    public static string Query(Film film, int? season, int? episode) => film.Kind == FilmKind.Movie
        ? $"?tmdbId={film.Id.ToString(CultureInfo.InvariantCulture)}&type=movie"
        : $"?tmdbId={film.Id.ToString(CultureInfo.InvariantCulture)}&type=tv&seasonId={season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}&episodeId={episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}";

    public static Uri? Decrypt(string sealedUrl, byte[]? key = null)
    {
        if (!sealedUrl.StartsWith("as_", StringComparison.Ordinal)) return null;
        byte[] data;
        try { data = Convert.FromHexString(sealedUrl[3..]); }
        catch (FormatException) { return null; }
        if (data.Length <= 28) return null;
        var plain = new byte[data.Length - 28];
        try
        {
            using var aes = new AesGcm(key ?? Key, 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, data.Length - 28), data.AsSpan(data.Length - 16), plain);
        }
        catch (CryptographicException) { return null; }
        return Uri.TryCreate(System.Text.Encoding.UTF8.GetString(plain), UriKind.Absolute, out var url) && url.Scheme == "https" ? url : null;
    }

    public static string? SealedFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object) return null;
        foreach (var source in sources.EnumerateObject())
            if (source.Value.ValueKind == JsonValueKind.Object && source.Value.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                return url.GetString();
        return null;
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var json = await http.TextAsync(new Uri(Api + Query(film, season, episode)), ct, open: true);
        if (SealedFrom(json) is not { } sealedUrl) throw new ProviderException("not listed");
        var stream = Decrypt(sealedUrl) ?? throw new ProviderException("the stream address could not be read");
        await VidlySource.EnsurePlaylistAsync(http, stream, Headers, ct);
        return [new ResolvedVideo(stream, "HLS", Headers, false, true, await VidlySource.SubtitlesAsync(http, film, season, episode, ct))];
    }
}

// vidbolt.xyz, asked for H.264 only: without it, it hands out the same HEVC stream Vidly does.
public sealed class VidBoltSource(SafeHttp http) : IFilmSource
{
    private static readonly Uri Origin = new("https://vidbolt.xyz/");
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["Referer"] = "https://vidbolt.xyz/", ["Origin"] = "https://vidbolt.xyz" };
    public string Id => "vidbolt";
    public string Name => "VidBolt";

    public static string Query(Film film, int? season, int? episode) => film.Kind == FilmKind.Movie
        ? $"api/resolve?type=movie&id={film.Id.ToString(CultureInfo.InvariantCulture)}&hevc=0"
        : $"api/resolve?type=tv&id={film.Id.ToString(CultureInfo.InvariantCulture)}&season={season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}&episode={episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}&hevc=0";

    // The first HLS source; a short preview clip is never taken for the episode.
    public static Uri? StreamFrom(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.Object
            || !stream.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return null;
        foreach (var source in sources.EnumerateArray())
        {
            if (source.ValueKind != JsonValueKind.Object || !source.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) continue;
            if (source.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() != "hls") continue;
            if (Uri.TryCreate(url.GetString(), UriKind.Absolute, out var link) && link.Scheme == "https"
                && !link.AbsoluteUri.Contains("preview", StringComparison.OrdinalIgnoreCase)) return link;
        }
        return null;
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var json = await http.TextAsync(new Uri(Origin, Query(film, season, episode)), ct, headers: Headers, open: true);
        var stream = StreamFrom(json) ?? throw new ProviderException("not listed");
        await VidlySource.EnsurePlaylistAsync(http, stream, Headers, ct);
        return [new ResolvedVideo(stream, "HLS", Headers, false, true, await VidlySource.SubtitlesAsync(http, film, season, episode, ct))];
    }
}

// punkflix.net, the player behind Cinezo and ArrowTV: plain JSON with the HLS address and English subtitle tracks.
// Strong on movies, thin on TV.
public sealed class PunkflixSource(SafeHttp http) : IFilmSource
{
    private static readonly Uri Origin = new("https://punkflix.net/");
    private static readonly IReadOnlyDictionary<string, string> Headers = new Dictionary<string, string> { ["Referer"] = "https://player.cinezo.live/", ["Origin"] = "https://player.cinezo.live" };
    public string Id => "punkflix";
    public string Name => "Punkflix";

    public static string Path(Film film, int? season, int? episode) => film.Kind == FilmKind.Movie
        ? $"movie/{film.Id.ToString(CultureInfo.InvariantCulture)}.json"
        : $"tv/{film.Id.ToString(CultureInfo.InvariantCulture)}/{season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}/{episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}.json";

    public static (Uri? Stream, IReadOnlyList<Subtitle> Subtitles) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Uri? stream = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("stream", out var links) && links.ValueKind == JsonValueKind.Object
            && links.TryGetProperty("hls", out var hls) && hls.ValueKind == JsonValueKind.String
            && Uri.TryCreate(hls.GetString(), UriKind.Absolute, out var url) && url.Scheme == "https") stream = url;
        var subtitles = new List<Subtitle>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("subtitles", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (Text("language") is not ("eng" or "en") || !Uri.TryCreate(Text("url"), UriKind.Absolute, out var track) || track.Scheme != "https") continue;
                subtitles.Add(new Subtitle(Text("title") ?? "English", "en", track));
            }
        return (stream, Languages.Arrange(subtitles));
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var (stream, subtitles) = Parse(await http.TextAsync(new Uri(Origin, Path(film, season, episode)), ct, headers: Headers, open: true));
        if (stream is null) throw new ProviderException("not listed");
        await VidlySource.EnsurePlaylistAsync(http, stream, Headers, ct);
        return [new ResolvedVideo(stream, "HLS", Headers, false, true, subtitles)];
    }
}

// VidRift, the player behind 7Movies and Ghostflix. 7Movies hands out a playback token; with it the player's boot
// answer carries BigBang (H.264, up to 4K) and Vexon/Evion (H.264, 1080p) links that need no headers. Without one only
// Orion (HEVC, 1080p) comes back, which still plays after a transcode.
public sealed class VidRiftSource(SafeHttp http) : IFilmSource
{
    private static readonly Uri Token = new("https://7movies.ac/api/playback-token");
    private static readonly Uri Player = new("https://embed.vidrift.net/");
    private static readonly IReadOnlyDictionary<string, string> Boot = new Dictionary<string, string> { ["Referer"] = "https://embed.vidrift.net/embed2/play", ["x-embed-parent"] = "https://7movies.ac" };
    private static readonly IReadOnlyDictionary<string, string> Orion = new Dictionary<string, string> { ["Referer"] = "https://embed.vidrift.net/" };
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();
    public string Id => "vidrift";
    public string Name => "VidRift";

    public static string TokenBody(Film film, int? season, int? episode) => film.Kind == FilmKind.Movie
        ? $"{{\"tmdbId\":{film.Id.ToString(CultureInfo.InvariantCulture)},\"type\":\"movie\"}}"
        : $"{{\"tmdbId\":{film.Id.ToString(CultureInfo.InvariantCulture)},\"type\":\"tv\",\"season\":{season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)},\"episode\":{episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}}}";

    public static string BootPath(Film film, int? season, int? episode, string? token)
    {
        var id = film.Id.ToString(CultureInfo.InvariantCulture);
        var (s, e) = (season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture), episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture));
        var path = film.Kind == FilmKind.Movie ? $"api/boot/movie/{id}?type=movie&id={id}" : $"api/boot/tv/{id}/{s}/{e}?type=tv&id={id}&season={s}&episode={e}";
        return path + "&source=0&provider=&shell=1" + (token is { Length: > 0 } ? "&token=" + Uri.EscapeDataString(token) : "");
    }

    public static string? ParseToken(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("token", out var token)
            && token.ValueKind == JsonValueKind.String && token.GetString() is { Length: > 0 and < 4096 } value ? value : null;
    }

    // The best stream in a boot answer: BigBang, then Vexon or Evion, then Orion's original-language track.
    public static (Uri Stream, IReadOnlyDictionary<string, string> Headers, string Quality, IReadOnlyList<Subtitle> Subtitles)? Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object) return null;
        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static Uri? Https(string? text, Uri? relativeTo = null) => text is { Length: > 0 } && (relativeTo is null ? Uri.TryCreate(text, UriKind.Absolute, out var u) : Uri.TryCreate(relativeTo, text, out u))
            && u.Scheme == "https" ? u : null;
        var subtitles = new List<Subtitle>();
        if (meta.TryGetProperty("bigbangSubtitles", out var bigbangSubs) && bigbangSubs.ValueKind == JsonValueKind.Array)
            foreach (var sub in bigbangSubs.EnumerateArray())
                if (Text(sub, "lang") == "en" && Https(Text(sub, "url")) is { } url) subtitles.Add(new Subtitle(Text(sub, "label") ?? "English", "en", url));
        if (root.TryGetProperty("shell", out var shell) && shell.ValueKind == JsonValueKind.Object && shell.TryGetProperty("subtitles", out var shellSubs) && shellSubs.ValueKind == JsonValueKind.Array)
            foreach (var sub in shellSubs.EnumerateArray())
                if (Text(sub, "lang") == "en" && Https(Text(sub, "url"), Player) is { } url && url.Host == Player.Host) subtitles.Add(new Subtitle(Text(sub, "label") ?? "English", "en", url));
        var tracks = Languages.Arrange(subtitles.Take(4));
        if (Https(Text(meta, "bigbangUrl")) is { } bigbang) return (bigbang, None, Text(meta, "bigbangCodec") is "hevc" ? "HLS HEVC" : "HLS H.264", tracks);
        if ((Https(Text(meta, "vexonUrl")) ?? Https(Text(meta, "evionUrl"))) is { } wide) return (wide, None, "HLS 1080p H.264", tracks);
        if (meta.TryGetProperty("orionStreams", out var orion) && orion.ValueKind == JsonValueKind.Array)
            foreach (var stream in orion.EnumerateArray())
                if (Text(stream, "name")?.Contains("Original", StringComparison.OrdinalIgnoreCase) == true && Https(Text(stream, "url"), Player) is { } url && url.Host == Player.Host)
                    return (url, Orion, "HLS HEVC", tracks);
        return null;
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        string? token = null;
        // Without a token VidRift still answers, with the HEVC stream only.
        try { token = ParseToken(await http.TextAsync(Token, ct, json: TokenBody(film, season, episode), headers: new Dictionary<string, string> { ["Origin"] = "https://7movies.ac" }, open: true, limit: TimeSpan.FromSeconds(10))); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        var found = Parse(await http.TextAsync(new Uri(Player, BootPath(film, season, episode, token)), ct, headers: Boot, open: true))
            ?? throw new ProviderException("not listed");
        await VidlySource.EnsurePlaylistAsync(http, found.Stream, found.Headers, ct);
        return [new ResolvedVideo(found.Stream, found.Quality, found.Headers, false, true, found.Subtitles)];
    }
}

// Rive (rivestream), behind CorsFlix too. Each request carries a key the site computes from the TMDB id with two small
// hashes and a table from its own code. Citadel (English audio only, up to 720p, no headers) is asked first, then
// PrimeVids, whose playlists want the site as referrer.
public sealed class RiveSource(SafeHttp http, Func<DateTimeOffset>? clock = null) : IFilmSource
{
    private static readonly Uri Api = new("https://www.rivestream.app/api/backendfetch");
    private static readonly IReadOnlyDictionary<string, string> Site = new Dictionary<string, string> { ["Referer"] = "https://www.rivestream.app/" };
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();
    private static readonly string[] Table = ["4Z7lUo", "gwIVSMD", "PLmz2elE2v", "Z4OFV0", "SZ6RZq6Zc", "zhJEFYxrz8", "FOm7b0", "axHS3q4KDq", "o9zuXQ", "4Aebt", "wgjjWwKKx", "rY4VIxqSN", "kfjbnSo", "2DyrFA1M", "YUixDM9B", "JQvgEj0", "mcuFx6JIek", "eoTKe26gL", "qaI9EVO1rB", "0xl33btZL", "1fszuAU", "a7jnHzst6P", "wQuJkX", "cBNhTJlEOf", "KNcFWhDvgT", "XipDGjST", "PCZJlbHoyt", "2AYnMZkqd", "HIpJh", "KH0C3iztrG", "W81hjts92", "rJhAT", "NON7LKoMQ", "NMdY3nsKzI", "t4En5v", "Qq5cOQ9H", "Y9nwrp", "VX5FYVfsf", "cE5SJG", "x1vj1", "HegbLe", "zJ3nmt4OA", "gt7rxW57dq", "clIE9b", "jyJ9g", "B5jXjMCSx", "cOzZBZTV", "FTXGy", "Dfh1q1", "ny9jqZ2POI", "X2NnMn", "MBtoyD", "qz4Ilys7wB", "68lbOMye", "3YUJnmxp", "1fv5Imona", "PlfvvXD7mA", "ZarKfHCaPR", "owORnX", "dQP1YU", "dVdkx", "qgiK0E", "cx9wQ", "5F9bGa", "7UjkKrp", "Yvhrj", "wYXez5Dg3", "pG4GMU", "MwMAu", "rFRD5wlM"];
    public string Id => "rive";
    public string Name => "Rive";

    public static string Key(int id)
    {
        var text = id.ToString(CultureInfo.InvariantCulture);
        var cut = id % text.Length / 2;
        return Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(Outer(Inner(text[..cut] + Table[id % Table.Length] + text[cut..]))));
    }

    // JavaScript's signed toString(16), padded to eight characters the way the site pads it.
    private static string JsHex(uint value)
    {
        var signed = unchecked((int)value);
        var hex = signed < 0 ? "-" + ((uint)-(long)signed).ToString("x", CultureInfo.InvariantCulture) : value.ToString("x", CultureInfo.InvariantCulture);
        return hex.PadLeft(8, '0');
    }
    private static uint Mul(uint n, uint k) => unchecked((n & 0xffff) * k + ((((n >> 16) * k) & 0xffff) << 16));
    private static uint MulWide(uint n, uint k) => unchecked((n & 0xffff) * k + (((n >> 16) * k) << 16));
    private static uint Rotl(uint n, int k) => k == 0 ? n : (n << k) | (n >> (32 - k));

    private static string Inner(string s)
    {
        uint t = 0;
        for (var n = 0; n < s.Length; n++)
        {
            uint r = s[n];
            t = unchecked(r + (t << 6) + (t << 16) - t);
            var i = Rotl(t, n % 5);
            var j = n % 7;
            var rr = unchecked((r << j) | (r >> (8 - j)));
            t ^= i ^ rr;
            t = unchecked(t + ((t >> 11) ^ (t << 3)));
        }
        t ^= t >> 15; t = Mul(t, 49842); t ^= t >> 13; t = Mul(t, 40503); t ^= t >> 16;
        return JsHex(t);
    }

    private static string Outer(string s)
    {
        var n = 3735928559u ^ (uint)s.Length;
        for (var e = 0; e < s.Length; e++)
        {
            uint r = s[e];
            r ^= (uint)((131 * e + 89) ^ (int)(r << (e % 5))) & 255;
            n = Rotl(n, 7) ^ r;
            n = MulWide(n, 60205);
            n ^= n >> 11;
        }
        n ^= n >> 15; n = MulWide(n, 49842); n ^= n >> 13; n = MulWide(n, 40503); n ^= n >> 16; n = MulWide(n, 10196); n ^= n >> 15;
        return JsHex(n);
    }

    public static string Query(Film film, int? season, int? episode, string service, DateTimeOffset now)
    {
        var id = film.Id.ToString(CultureInfo.InvariantCulture);
        var what = film.Kind == FilmKind.Movie ? $"requestID=movieVideoProvider&id={id}"
            : $"requestID=tvVideoProvider&id={id}&season={season.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}&episode={episode.GetValueOrDefault(1).ToString(CultureInfo.InvariantCulture)}";
        return $"?{what}&service={service}&cb={(now.ToUnixTimeMilliseconds() / 3_000_000).ToString(CultureInfo.InvariantCulture)}&secretKey={Uri.EscapeDataString(Key(film.Id))}&proxyMode=noProxy";
    }

    // The tallest English stream (Citadel labels each "720p | English"), or the first stream when none says its language.
    public static (Uri Stream, string Quality, IReadOnlyList<Subtitle> Subtitles)? Parse(string json, bool englishOnly)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
        static string? Text(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var streams = new List<(Uri Url, string Quality)>();
        if (data.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var source in sources.EnumerateArray())
            {
                var quality = Text(source, "quality") ?? "";
                if (Text(source, "format") is { } format && format != "hls") continue;
                if (englishOnly && !quality.Contains("English", StringComparison.OrdinalIgnoreCase)) continue;
                if (Uri.TryCreate(Text(source, "url"), UriKind.Absolute, out var url) && url.Scheme == "https") streams.Add((url, quality));
            }
        if (streams.Count == 0) return null;
        var best = streams.OrderByDescending(s => FilmSources.LabelHeight(s.Quality)).First();
        var subtitles = new List<Subtitle>();
        if (data.TryGetProperty("captions", out var captions) && captions.ValueKind == JsonValueKind.Array)
            foreach (var caption in captions.EnumerateArray())
                if (Text(caption, "language")?.StartsWith("English", StringComparison.OrdinalIgnoreCase) == true && Uri.TryCreate(Text(caption, "file"), UriKind.Absolute, out var file) && file.Scheme == "https")
                    subtitles.Add(new Subtitle("English", "en", file));
        return (best.Url, FilmSources.LabelHeight(best.Quality) is > 0 and var h ? h.ToString(CultureInfo.InvariantCulture) + "p" : "HLS", Languages.Arrange(subtitles.DistinctBy(s => s.Url).Take(3)));
    }

    public async Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        foreach (var (service, headers, englishOnly) in new[] { ("citadel", None, true), ("primevids", Site, false) })
        {
            string json;
            try { json = await http.TextAsync(new Uri(Api + Query(film, season, episode, service, now)), ct, open: true, limit: TimeSpan.FromSeconds(15)); }
            catch (Exception ex) when (ex is SourceStatusException or TaskCanceledException && !ct.IsCancellationRequested) { continue; }
            if (Parse(json, englishOnly) is not { } found) continue;
            try { await VidlySource.EnsurePlaylistAsync(http, found.Stream, headers, ct); }
            catch (Exception ex) when (ex is ProviderException or SourceStatusException) { continue; }
            return [new ResolvedVideo(found.Stream, found.Quality, headers, false, true, found.Subtitles)];
        }
        throw new ProviderException("not listed");
    }
}
