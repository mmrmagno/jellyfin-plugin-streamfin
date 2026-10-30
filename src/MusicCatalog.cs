using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

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
public sealed class MusicCatalog(SafeHttp http, MusicMetadataStore? disk = null, ILogger<MusicCatalog>? logger = null) : IDisposable
{
    public const string DefaultApi = "https://tracks.monochrome.st/";
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 8000 });
    // Songs the catalog has described in any answer (search hits, top tracks, album tracks), by id. A queue or a
    // scrobble of a song from a search then needs no lookup of its whole album.
    private readonly MemoryCache tracks = new(new MemoryCacheOptions { SizeLimit = 20_000 });
    private const int Budget = 20;
    private readonly RateWindow budget = new(Budget, TimeSpan.FromSeconds(10));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<object>> loading = new();
    private readonly CatalogBreaker breaker = new();
    // The budget limits how often calls start; with 20 s answers that alone would let them pile up.
    private readonly SemaphoreSlim flight = new(6, 6);
    // Refreshes of stored copies run behind the answer; two at most, and only while foreground calls have room.
    private readonly SemaphoreSlim refreshes = new(2, 2);
    // On a bad day the catalog answers after 20-30 s and Cloudflare gives up at about 40 s with a 522. A call may
    // take that long, so a slow answer still lands in the cache; a caller waits less, so an app (or the proxy in
    // front of Jellyfin, usually 60 s) gets an answer it can show, and its retry finds the cached result.
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan CallerLimit = TimeSpan.FromSeconds(25);
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
        return CachedAsync("search/" + text.ToLowerInvariant(), TimeSpan.FromMinutes(30), "search?q=" + Uri.EscapeDataString(text), ParseSearch, null, null, ct);
    }

    public Task<MusicRelease> ReleaseAsync(string id, CancellationToken ct) => CachedAsync("release/" + ValidateId(id), Lifetime, "releases/" + id, ParseRelease,
        "release", id, ct);

    public MusicTrack? KnownTrack(string trackId) => tracks.TryGetValue<MusicTrack>(trackId, out var track) ? track : null;

    private void Learn(object value)
    {
        IEnumerable<MusicTrack> found = value switch
        {
            MusicResults results => results.Tracks,
            MusicArtistPage artist => artist.TopTracks,
            MusicRelease release => release.Tracks.Select(t => t with { Artwork = t.Artwork ?? release.Artwork }),
            _ => []
        };
        foreach (var track in found)
            tracks.Set(track.Id, track, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromDays(1) });
    }

    // Whether a lookup would be answered without asking the catalog: fresh, or a stored copy that answers at once.
    public bool HasRelease(string id) => IsId(id) && (Known("release/" + id) || disk?.Has("release", id) == true);
    public bool HasArtist(string id) => IsId(id) && (Known("artist/" + id) || disk?.Has("artist", id) == true);
    private bool HasSearch(string text) => Known("search/" + text.Trim().ToLowerInvariant());
    private bool Known(string key) => cache.TryGetValue($"{Revision}/{key}", out _) || cache.TryGetValue(Stale(key), out _);
    private string Stale(string key) => $"stale/{Revision}/{ApiHost}/{key}";
    // Counted as down after a few failures in a row; lookups then use stored copies or fail at once.
    public bool Down => breaker.IsOpen(DateTime.UtcNow);
    // Extras (explicit twins in a list, album names on search hits) only spend the catalog budget while at
    // least half of it is free. Apps that search as you type would otherwise starve opening an album or playing.
    public bool Spare => budget.Free(DateTime.UtcNow) > Budget / 2;

    public Task<MusicArtistPage> ArtistAsync(string id, CancellationToken ct) => CachedAsync("artist/" + ValidateId(id), Lifetime, "artists/" + id, ParseArtist,
        "artist", id, ct);

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
    // patience bounds the wait on playback paths; the search carries on and is cached for next time.
    public async Task<MusicTrack> ExplicitAsync(MusicTrack track, CancellationToken ct, TimeSpan? patience = null)
    {
        if (track.Explicit || track.Artists.Count == 0) return track;
        try
        {
            var results = await SearchAsync(track.Title + " " + track.Artists[0].Name, ct).WaitAsync(patience ?? Timeout.InfiniteTimeSpan, ct);
            return results.Tracks.FirstOrDefault(t => t.Explicit && SameSong(t, track)) ?? track;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException or TimeoutException
            or OperationCanceledException && !ct.IsCancellationRequested) { return track; }
    }

    // A list can hold a clean edit whose explicit twin it does not contain (a search that matched only the
    // edit, an artist's top tracks). The first few are looked up, so lists show what will actually play.
    // Extras like these get a few seconds in all, however slow the catalog is: a list a moment less polished beats
    // one that arrives after the app gave up. What is looked up keeps loading and is there next time.
    public static readonly TimeSpan ExtraTime = TimeSpan.FromSeconds(5);
    private static TimeSpan Left(DateTime deadline) => deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    public async Task<IReadOnlyList<MusicTrack>> ExplicitTracksAsync(IReadOnlyList<MusicTrack> tracks, CancellationToken ct, int lookups = 5)
    {
        if (!PreferExplicit) return tracks;
        var kept = WithoutCleanCopies(tracks).ToArray();
        var deadline = DateTime.UtcNow + ExtraTime;
        for (var i = 0; i < kept.Length && lookups > 0; i++)
        {
            if (kept[i].Explicit || kept[i].Artists.Count == 0) continue;
            var cached = HasSearch(kept[i].Title + " " + kept[i].Artists[0].Name);
            if (!cached && (!Spare || Down || Left(deadline) == TimeSpan.Zero)) break;
            lookups--;
            kept[i] = await ExplicitAsync(kept[i], ct, cached ? null : Left(deadline));
        }
        return kept.DistinctBy(t => t.Isrc ?? t.Id).ToArray();
    }

    // Same title, artists, type and length: load the copies (a few, cached) and drop the ones with no explicit track.
    public async Task<IReadOnlyList<MusicRelease>> WithoutCleanCopiesAsync(IReadOnlyList<MusicRelease> releases, CancellationToken ct)
    {
        var clean = new HashSet<string>();
        var deadline = DateTime.UtcNow + ExtraTime;
        foreach (var group in releases.GroupBy(r => (Song(r.Title), Credits(r.Artists), r.Type, r.TrackCount)).Where(g => g.Count() > 1).Take(5))
        {
            var loaded = new List<(string Id, bool Explicit)>();
            foreach (var release in group.Take(4))
            {
                if (release.Tracks.Count == 0 && !HasRelease(release.Id) && (!Spare || Down || Left(deadline) == TimeSpan.Zero)) { loaded.Clear(); break; }
                try
                {
                    var full = release.Tracks.Count > 0 ? release : await ReleaseAsync(release.Id, ct).WaitAsync(HasRelease(release.Id) ? Timeout.InfiniteTimeSpan : Left(deadline), ct);
                    loaded.Add((release.Id, full.Tracks.Any(t => t.Explicit)));
                }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TimeoutException
                    or OperationCanceledException && !ct.IsCancellationRequested) { loaded.Clear(); break; }
            }
            if (loaded.Any(r => r.Explicit)) clean.UnionWith(loaded.Where(r => !r.Explicit).Select(r => r.Id));
        }
        return clean.Count == 0 ? releases : releases.Where(r => !clean.Contains(r.Id)).ToArray();
    }

    // Fresh copies answer first. Then a stored one (memory, or disk for releases and artists) answers at once while
    // a refresh runs behind it, so a slow or broken catalog costs nothing for anything seen before. Only what was
    // never seen waits for the catalog, and while it is counted as down that fails at once.
    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, string path, Func<string, T> parse, string? kind, string? id,
        CancellationToken ct) where T : class
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        if (Stored(key, parse, kind, id) is { } stored)
        {
            if (Spare && !Down && flight.CurrentCount > 3) _ = RefreshAsync(key, lifetime, path, parse, kind, id);
            return stored;
        }
        try { return (T)await LoadAsync(key, lifetime, path, parse, kind, id).WaitAsync(CallerLimit, ct); }
        catch (TimeoutException) { throw new ProviderException("The music catalog is answering slowly right now. Please try again in a moment."); }
    }

    private T? Stored<T>(string key, Func<string, T> parse, string? kind, string? id) where T : class
    {
        if (cache.TryGetValue<T>(Stale(key), out var stale)) return stale;
        if (kind is null || id is null || disk?.Read(kind, id) is not { } json) return null;
        try
        {
            var value = parse(json);
            cache.Set(Stale(key), value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            Learn(value);
            return value;
        }
        catch (Exception ex) when (ex is ProviderException or JsonException or RegexMatchTimeoutException)
        {
            disk?.Forget(kind, id);
            return null;
        }
    }

    private async Task RefreshAsync<T>(string key, TimeSpan lifetime, string path, Func<string, T> parse, string? kind, string? id) where T : class
    {
        if (!refreshes.Wait(0)) return;
        try { await LoadAsync(key, lifetime, path, parse, kind, id); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException or OperationCanceledException
            or RegexMatchTimeoutException) { }
        finally { refreshes.Release(); }
    }

    // Requests that arrive together for the same thing share one catalog call. It runs on its own deadline, not a
    // caller's, so an app that gives up early still leaves the answer cached for its retry.
    private Task<object> LoadAsync<T>(string key, TimeSpan lifetime, string path, Func<string, T> parse, string? kind, string? id) where T : class
    {
        var slot = $"{Revision}/{key}";
        var shared = loading.GetOrAdd(slot, name =>
        {
            var call = Task.Run(async () =>
            {
                var json = await QueryAsync(path);
                var value = parse(json);
                Learn(value);
                cache.Set(slot, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
                cache.Set(Stale(key), value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
                if (kind is not null && id is not null && disk is not null) await disk.WriteAsync(kind, id, json);
                return (object)value;
            });
            // Also observes the failure of a call every caller gave up on.
            _ = call.ContinueWith(done => { _ = done.Exception; loading.TryRemove(new KeyValuePair<string, Task<object>>(slot, done)); }, TaskScheduler.Default);
            return call;
        });
        // A call that failed at once (catalog counted as down) can finish before GetOrAdd publishes it, after its
        // own removal ran; left there, it would answer every later request with that old failure.
        if (shared.IsCompleted) loading.TryRemove(new KeyValuePair<string, Task<object>>(slot, shared));
        return shared;
    }

    private async Task<string> QueryAsync(string path)
    {
        var api = Api ?? throw new ProviderException("The music API address is invalid. Check it under Dashboard → Plugins → StreamFin.");
        if (Down && !breaker.Due(DateTime.UtcNow)) throw new CatalogDownException();
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("The music catalog is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        if (!await flight.WaitAsync(TimeSpan.FromSeconds(10))) throw new RateLimitedException("The music catalog is busy. Please try again shortly.", null);
        // Taken last, so the one check per period is only spent on a call that really goes out.
        if (!breaker.Allow(DateTime.UtcNow))
        {
            flight.Release();
            throw new CatalogDownException();
        }
        try
        {
            var text = await http.TextAsync(new Uri(api, path), CancellationToken.None, limit: CallLimit);
            if (breaker.Success()) logger?.LogInformation("StreamFin music catalog answers again");
            return text;
        }
        catch (RateLimitedException)
        {
            budget.Exhaust(DateTime.UtcNow, TimeSpan.FromSeconds(30));
            throw;
        }
        catch (SourceStatusException ex) when (ex.Status is 404 or 410) { throw new ProviderException("The music catalog no longer has this."); }
        catch (Exception ex) when (CatalogBreaker.IsOutage(ex, CancellationToken.None))
        {
            if (breaker.Failure(DateTime.UtcNow))
                logger?.LogWarning("StreamFin music catalog is not responding ({Cause}); serving stored copies until it is back",
                    ex is SourceStatusException status ? "HTTP " + status.Status : ex.GetType().Name);
            throw new CatalogDownException();
        }
        finally { flight.Release(); }
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

    public void Dispose()
    {
        cache.Dispose();
        tracks.Dispose();
        flight.Dispose();
        refreshes.Dispose();
    }
}
