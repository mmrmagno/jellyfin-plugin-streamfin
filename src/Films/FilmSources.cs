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

// Movies & TV sources in order: FlixHQ first, then the next one whenever a site does not list the title or cannot play
// it. A site that is down is tried last instead of first, and the one that worked is returned, so an expired link is
// looked up on the same site again.
public sealed class FilmSources(IEnumerable<IFilmSource> sources, VideoHealth health)
{
    public static readonly string[] Order = ["flix", "astra", "vidly", "vidbolt", "punkflix"];
    public static bool IsEnabled(string id) => id switch
    {
        "vidly" => Plugin.Instance?.Configuration.FilmVidly != false,
        "astra" => Plugin.Instance?.Configuration.FilmAstra != false,
        "vidbolt" => Plugin.Instance?.Configuration.FilmVidBolt != false,
        "punkflix" => Plugin.Instance?.Configuration.FilmPunkflix != false,
        _ => true
    };

    public IReadOnlyList<IFilmSource> Enabled => sources.Where(s => IsEnabled(s.Id))
        .OrderBy(s => health.Usable(s.Id) ? 0 : 1).ThenBy(s => Array.IndexOf(Order, s.Id) is var i and >= 0 ? i : Order.Length).ToArray();

    public async Task<(IFilmSource Source, IReadOnlyList<ResolvedVideo> Videos)> ResolveAsync(Film film, int? season, int? episode, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var source in Enabled)
        {
            try
            {
                var videos = await source.ResolveAsync(film, season, episode, ct);
                if (videos.Count > 0) return (source, videos);
                errors.Add(source.Name + ": no video");
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or JsonException or UriFormatException
                or RegexMatchTimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                health.Failed(source.Id, ex, ct);
                errors.Add(source.Name + ": " + (ex is SourceStatusException { Status: 404 } ? "not listed"
                    : ex is SourceStatusException status ? $"HTTP {status.Status}" : ex is ProviderException ? ex.Message.TrimEnd('.') : "unavailable"));
            }
        }
        throw new ProviderException(errors.Count == 0 ? "Every Movies & TV source is switched off in the settings."
            : "No source could play this title right now (" + string.Join("; ", errors) + "). Please try again later.");
    }
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
