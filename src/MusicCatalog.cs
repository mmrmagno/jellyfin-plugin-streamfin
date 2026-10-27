using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record MusicArtistRef(string Id, string Name);
public sealed record MusicTrack(string Id, string Title, IReadOnlyList<MusicArtistRef> Artists, string ReleaseId, string? Artwork, int? DurationMs,
    string? Isrc, bool Explicit, int? Disc, int? Number);
public sealed record MusicRelease(string Id, string Title, IReadOnlyList<MusicArtistRef> Artists, string Type, DateTime? Date, string? Label,
    string? Artwork, int? TrackCount, IReadOnlyList<MusicTrack> Tracks);
public sealed record MusicArtistPage(string Id, string Name, string? Image, string Bio, IReadOnlyList<MusicTrack> TopTracks, IReadOnlyList<MusicRelease> Releases);
public sealed record MusicResults(IReadOnlyList<MusicTrack> Tracks, IReadOnlyList<MusicRelease> Releases, IReadOnlyList<MusicArtistPage> Artists);

// Lossless catalog: Monochrome's tracks API. Search and release JSON, and GET /track/<id> answers
// with the FLAC itself (16/44.1, some masters 24 bit; byte ranges, immutable), so a stream needs no resolving step.
public sealed class MusicCatalog(SafeHttp http) : IDisposable
{
    public const string DefaultApi = "https://tracks.monochrome.st/";
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4000 });
    private readonly RateWindow budget = new(20, TimeSpan.FromSeconds(10));
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440));

    // A self-hosted or mirror instance can replace the default. Only a bare https origin on 443 is
    // accepted, because its host joins the fixed list of sites SafeHttp talks to by name.
    public static Uri? ParseApi(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new Uri(DefaultApi);
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || uri.Port != 443
            || uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.') || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        return new Uri("https://" + uri.IdnHost.ToLowerInvariant() + "/");
    }
    public static Uri? Api => ParseApi(Plugin.Instance?.Configuration.MusicApiUrl);
    public static string ApiHost => Api?.IdnHost ?? "";
    public static bool IsId(string? id) => id is not null && Regex.IsMatch(id, @"\A[0-9]{15,20}\z", RegexOptions.None, TimeSpan.FromSeconds(1));
    public static string ValidateId(string? id) => IsId(id) ? id! : throw new ProviderException("Invalid music identifier.");
    public static Uri StreamUri(string trackId) => new(Api ?? throw new ProviderException("The music API address is invalid."), "track/" + ValidateId(trackId));

    public Task<MusicResults> SearchAsync(string query, CancellationToken ct)
    {
        var text = query.Trim();
        if (text.Length is < 2 or > 100) return Task.FromResult(new MusicResults([], [], []));
        return CachedAsync("search/" + text.ToLowerInvariant(), TimeSpan.FromMinutes(30),
            async () => ParseSearch(await QueryAsync("search?q=" + Uri.EscapeDataString(text), ct)));
    }

    public Task<MusicRelease> ReleaseAsync(string id, CancellationToken ct) => CachedAsync("release/" + ValidateId(id), Lifetime,
        async () => ParseRelease(await QueryAsync("releases/" + id, ct)));

    public Task<MusicArtistPage> ArtistAsync(string id, CancellationToken ct) => CachedAsync("artist/" + ValidateId(id), Lifetime,
        async () => ParseArtist(await QueryAsync("artists/" + id, ct)));

    public async Task<MusicTrack> TrackAsync(string releaseId, string trackId, CancellationToken ct)
        => (await ReleaseAsync(releaseId, ct)).Tracks.FirstOrDefault(t => t.Id == ValidateId(trackId)) ?? throw new ProviderException("This track is unavailable.");

    // Labels publish an edited copy next to the explicit one: same title, artists and length, its own ISRC.
    // Only tracks carry the flag (every release says explicit: false), so albums are judged by their tracks.
    public static bool PreferExplicit => Plugin.Instance?.Configuration.MusicPreferExplicit ?? true;
    private static string Song(string title) => Regex.Replace(title.ToLowerInvariant(), @"\s*[(\[-]\s*(?:clean|edited|radio edit)(?: version)?\s*[)\]]?\s*\z", "",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
    private static string Credits(IReadOnlyList<MusicArtistRef> artists) => string.Join(",", artists.Select(a => a.Id).Order(StringComparer.Ordinal));
    public static bool SameSong(MusicTrack a, MusicTrack b) => a.Id != b.Id && a.Artists.Count > 0 && Song(a.Title) == Song(b.Title)
        && Credits(a.Artists) == Credits(b.Artists) && a.DurationMs is int x && b.DurationMs is int y && Math.Abs(x - y) <= 2000;
    // The same recording also turns up once per release it is on; its ISRC tells.
    public static IReadOnlyList<MusicTrack> WithoutCleanCopies(IReadOnlyList<MusicTrack> tracks)
        => tracks.Where(t => t.Explicit || !tracks.Any(e => e.Explicit && SameSong(e, t))).DistinctBy(t => t.Isrc ?? t.Id).ToArray();

    // A clean track reached through a list that had no twin (top tracks, an edited album, history) plays the explicit recording.
    public async Task<MusicTrack> ExplicitAsync(MusicTrack track, CancellationToken ct)
    {
        if (track.Explicit || track.Artists.Count == 0) return track;
        try { return (await SearchAsync(track.Title + " " + track.Artists[0].Name, ct)).Tracks.FirstOrDefault(t => t.Explicit && SameSong(t, track)) ?? track; }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException
            or TaskCanceledException && !ct.IsCancellationRequested) { return track; }
    }

    // Same title, artists, type and length: load the copies (a few, cached) and drop the ones with no explicit track.
    public async Task<IReadOnlyList<MusicRelease>> WithoutCleanCopiesAsync(IReadOnlyList<MusicRelease> releases, CancellationToken ct)
    {
        var clean = new HashSet<string>();
        foreach (var group in releases.GroupBy(r => (Song(r.Title), Credits(r.Artists), r.Type, r.TrackCount)).Where(g => g.Count() > 1).Take(5))
        {
            var loaded = new List<(string Id, bool Explicit)>();
            foreach (var release in group.Take(4))
            {
                try { loaded.Add((release.Id, (release.Tracks.Count > 0 ? release : await ReleaseAsync(release.Id, ct)).Tracks.Any(t => t.Explicit))); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException
                    or TaskCanceledException && !ct.IsCancellationRequested) { loaded.Clear(); break; }
            }
            if (loaded.Any(r => r.Explicit)) clean.UnionWith(loaded.Where(r => !r.Explicit).Select(r => r.Id));
        }
        return clean.Count == 0 ? releases : releases.Where(r => !clean.Contains(r.Id)).ToArray();
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<Task<T>> load)
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        try
        {
            var value = await load();
            cache.Set($"{Revision}/{key}", value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
            cache.Set("stale/" + key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            return value;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException
            && cache.TryGetValue<T>("stale/" + key, out var stale)) { return stale!; }
    }

    private async Task<string> QueryAsync(string path, CancellationToken ct)
    {
        var api = Api ?? throw new ProviderException("The music API address is invalid. Check it under Dashboard → Plugins → StreamFin.");
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("The music catalog is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        return await http.TextAsync(new Uri(api, path), ct);
    }

    // Text is plain; angle brackets are dropped so no client can mistake it for markup.
    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim() is { Length: > 0 } s ? s : null;
    private static int? Number(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= 0 ? n : null;
    private static string? Id(JsonElement e, params string[] keys) => keys.Select(key => Text(e, key)).FirstOrDefault(IsId);
    private static IEnumerable<JsonElement> Array(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    // Artwork is fetched by Jellyfin and by Subsonic clients, so it must stay on the catalog host. The address
    // is rebuilt from the checked parts: an empty "https://@host" passes as no user info but survives in AbsoluteUri.
    private static string? Artwork(JsonElement e, string key) => Text(e, key) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.IdnHost == ApiHost && uri.Query.Length == 0
        && Regex.IsMatch(uri.AbsolutePath, @"\A/proxy/[A-Za-z0-9/_.-]{1,200}\.(?:jpg|png|webp)\z", RegexOptions.None, TimeSpan.FromSeconds(1))
        ? "https://" + ApiHost + uri.AbsolutePath : null;

    // Search hits carry artistIds/artistNames; releases and artist pages nest artist objects.
    private static IReadOnlyList<MusicArtistRef> Artists(JsonElement e)
    {
        var nested = Array(e, "artists").Select(a => Id(a, "artistId", "id") is { } id && (Text(a, "name") ?? Text(a, "displayName")) is { } name
            ? new MusicArtistRef(id, name) : null).OfType<MusicArtistRef>();
        var ids = Array(e, "artistIds").Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : null).ToArray();
        var names = Array(e, "artistNames").Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()?.Replace("<", "").Replace(">", "").Trim() : null).ToArray();
        var flat = ids.Zip(names).Where(p => IsId(p.First) && !string.IsNullOrEmpty(p.Second)).Select(p => new MusicArtistRef(p.First!, p.Second!));
        return nested.Concat(flat).DistinctBy(a => a.Id).Take(20).ToArray();
    }

    // Inside a release the release's own ID wins: a track is later looked up in the release its ID names.
    public static MusicTrack? ParseTrack(JsonElement e, string? releaseId = null)
    {
        if (Id(e, "trackId", "id") is not { } id || Text(e, "title") is not { } title) return null;
        if (e.TryGetProperty("playable", out var playable) && playable.ValueKind == JsonValueKind.False) return null;
        if ((IsId(releaseId) ? releaseId : Id(e, "releaseId")) is not { } release) return null;
        var isrc = Text(e, "isrc") is { } code && Regex.IsMatch(code, @"\A[A-Z0-9]{12}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? code : null;
        return new MusicTrack(id, title, Artists(e), release, Artwork(e, "artwork"), Number(e, "duration"), isrc,
            e.TryGetProperty("explicit", out var explicitFlag) && explicitFlag.ValueKind == JsonValueKind.True,
            Number(e, "discNumber") is int disc and > 0 and < 100 ? disc : null, Number(e, "trackNumber") is int number and > 0 and < 1000 ? number : null);
    }

    public static MusicRelease? ParseReleaseNode(JsonElement e)
    {
        if (Id(e, "releaseId", "id") is not { } id || Text(e, "title") is not { } title) return null;
        var date = DateTime.TryParse(Text(e, "releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            && parsed.Year is > 1800 and < 2200 ? parsed : (DateTime?)null;
        var type = Text(e, "releaseType")?.ToUpperInvariant() is "ALBUM" or "SINGLE" or "EP" or "COMPILATION" ? Text(e, "releaseType")!.ToUpperInvariant() : "ALBUM";
        var tracks = Array(e, "tracks").Select(t => ParseTrack(t, id)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(500)
            .OrderBy(t => t.Disc ?? 1).ThenBy(t => t.Number ?? int.MaxValue).ToArray();
        return new MusicRelease(id, title, Artists(e), type, date, Text(e, "label"), Artwork(e, "artwork"), Number(e, "trackCount"), tracks);
    }

    private static MusicArtistPage? ParseArtistNode(JsonElement e)
    {
        if (Id(e, "artistId", "id") is not { } id || (Text(e, "name") ?? Text(e, "displayName")) is not { } name) return null;
        // "releases" mixes every type; the typed lists can hold releases it left out.
        var releases = new[] { "albums", "releases", "singles", "compilations" }.SelectMany(key => Array(e, key)).Select(ParseReleaseNode).OfType<MusicRelease>()
            .DistinctBy(r => r.Id).Take(200).ToArray();
        var top = Array(e, "topTracks").Select(t => ParseTrack(t)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(50).ToArray();
        return new MusicArtistPage(id, name, Artwork(e, "avatar"), Text(e, "bio") ?? Text(e, "biography") ?? "", top, releases);
    }

    private static JsonElement Root(JsonDocument document) => document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement
        : throw new ProviderException("The music catalog returned an unexpected response.");

    public static MusicResults ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = Root(document);
        return new MusicResults(Array(root, "tracks").Select(t => ParseTrack(t)).OfType<MusicTrack>().DistinctBy(t => t.Id).Take(50).ToArray(),
            Array(root, "releases").Select(ParseReleaseNode).OfType<MusicRelease>().DistinctBy(r => r.Id).Take(50).ToArray(),
            Array(root, "artists").Select(ParseArtistNode).OfType<MusicArtistPage>().DistinctBy(a => a.Id).Take(50).ToArray());
    }

    public static MusicRelease ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseReleaseNode(Root(document)) ?? throw new ProviderException("This album is unavailable.");
    }

    public static MusicArtistPage ParseArtist(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseArtistNode(Root(document)) ?? throw new ProviderException("This artist is unavailable.");
    }

    public void Dispose() => cache.Dispose();
}
