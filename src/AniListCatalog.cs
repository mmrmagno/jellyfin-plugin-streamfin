using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// AniList allows 90 requests a minute and drops to 30 when degraded. A single listing can
// ask about dozens of titles, so every query passes one shared budget, identical queries
// share one request, and a recent copy is served when the budget or AniList runs out.
public sealed class AniListCatalog(SafeHttp http, ILogger<AniListCatalog> logger) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4000 });
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> inflight = new();
    private readonly RateWindow budget = new(25, TimeSpan.FromMinutes(1));
    // Typing a search fires a query per pause. Searches get part of the budget and never wait,
    // so browsing a page always has slots left.
    private readonly RateWindow searches = new(15, TimeSpan.FromMinutes(1));
    private const string Fields = "id idMal title { romaji english native } synonyms startDate { year } description(asHtml:false) coverImage { large } duration isAdult genres episodes nextAiringEpisode { episode }";
    private static readonly Uri Endpoint = new("https://graphql.anilist.co");
    private static readonly TimeSpan StaleFor = TimeSpan.FromHours(24);
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440));
    // Classification and titles rarely change, and every content check reads them.
    private TimeSpan TitleLifetime => Lifetime < TimeSpan.FromHours(6) ? TimeSpan.FromHours(6) : Lifetime;
    public static bool IsAllowed(Anime anime, bool hideAdult, bool hideEcchi) =>
        !(hideAdult && anime.IsAdult) && !(hideEcchi && anime.Genres.Contains("Ecchi", StringComparer.OrdinalIgnoreCase));
    public static bool IsAllowed(Anime anime) => IsAllowed(anime,
        Plugin.Instance?.Configuration.HideAdultContent ?? true, Plugin.Instance?.Configuration.HideEcchiContent ?? true);
    public static string PolicyKey => $"{Plugin.Instance?.Configuration.HideAdultContent ?? true}-{Plugin.Instance?.Configuration.HideEcchiContent ?? true}";
    private static string Filters => (Plugin.Instance?.Configuration.HideAdultContent != false ? ",isAdult:false" : "")
        + (Plugin.Instance?.Configuration.HideEcchiContent != false ? ",genre_not_in:[\"Ecchi\"]" : "");

    public static (string Season, int Year) CurrentSeason(DateTime date) =>
        (date.Month switch { <= 3 => "WINTER", <= 6 => "SPRING", <= 9 => "SUMMER", _ => "FALL" }, date.Year);

    public async Task<CatalogPage> BrowseAsync(string category, int page, CancellationToken ct, string? search = null)
    {
        if (category is not ("popular" or "season" or "az" or "search") || page is < 1 or > 1000)
            throw new ProviderException("Invalid catalog page.");
        if (category == "search" && (string.IsNullOrWhiteSpace(search) || search.Trim().Length is < 2 or > 100))
            return new CatalogPage([], false);
        var season = CurrentSeason(DateTime.UtcNow);
        var key = $"{PolicyKey}/{category}/{page}/{season}/{search?.Trim().ToLowerInvariant()}";
        if (cache.TryGetValue<CatalogPage>($"{Revision}/{key}", out var found)) return found!;
        var order = category == "search" ? "SEARCH_MATCH" : category == "az" ? "TITLE_ROMAJI" : "POPULARITY_DESC";
        var seasonal = category == "season" ? $",season:{season.Season},seasonYear:{season.Year}" : "";
        var query = "query($page:Int" + (category == "search" ? ",$search:String" : "") + "){Page(page:$page,perPage:30){pageInfo{hasNextPage}media(type:ANIME" + Filters + ",sort:" + order + seasonal + (category == "search" ? ",search:$search" : "") + "){" + Fields + "}}}";
        try
        {
            using var document = await QueryAsync(query, new { page, search = search?.Trim() }, ct, category == "search");
            var root = document.RootElement.GetProperty("data").GetProperty("Page");
            var items = root.GetProperty("media").EnumerateArray().Select(Parse).Where(IsAllowed).ToArray();
            foreach (var item in items) Remember(item);
            var result = new CatalogPage(items, root.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
            // Searches are typed a letter at a time; keep them longer so retyping costs nothing.
            Store(key, result, category == "search" ? TimeSpan.FromMinutes(30) : Lifetime);
            return result;
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && cache.TryGetValue<CatalogPage>("stale/" + key, out var stale)) { return stale!; }
    }

    public async Task<Anime> GetAsync(int id, CancellationToken ct)
    {
        if (id <= 0) throw new ProviderException("Invalid anime identifier.");
        if (cache.TryGetValue<Anime>($"{Revision}/anime/{id}", out var found)) return found!;
        try
        {
            using var document = await QueryAsync("query($id:Int){Media(id:$id,type:ANIME){" + Fields + "}}", new { id }, ct);
            var node = document.RootElement.GetProperty("data").GetProperty("Media");
            if (node.ValueKind != JsonValueKind.Object) throw new ProviderException("This title is unavailable.");
            var anime = Parse(node);
            Remember(anime);
            return anime;
        }
        catch (Exception ex) when (IsUpstreamFailure(ex) && cache.TryGetValue<Anime>("stale/anime/" + id, out var stale)) { return stale!; }
    }

    // One request for a whole listing instead of one per title.
    public async Task PrefetchAsync(IEnumerable<int> ids, CancellationToken ct)
    {
        var missing = ids.Where(id => id > 0 && !cache.TryGetValue($"{Revision}/anime/{id}", out _)).Distinct().ToArray();
        foreach (var chunk in missing.Chunk(50))
        {
            try
            {
                using var document = await QueryAsync("query($ids:[Int]){Page(perPage:50){media(id_in:$ids,type:ANIME){" + Fields + "}}}", new { ids = chunk }, ct);
                foreach (var node in document.RootElement.GetProperty("data").GetProperty("Page").GetProperty("media").EnumerateArray())
                    Remember(Parse(node));
            }
            catch (Exception ex) when (IsUpstreamFailure(ex)) { return; }
        }
    }

    private void Remember(Anime anime) => Store("anime/" + anime.Id, anime, TitleLifetime);
    private void Store<T>(string key, T value, TimeSpan lifetime)
    {
        cache.Set($"{Revision}/{key}", value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        cache.Set("stale/" + key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = StaleFor });
    }
    private static bool IsUpstreamFailure(Exception ex) => ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException;

    private async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken ct, bool search = false)
    {
        if (search && searches.Reserve(DateTime.UtcNow, TimeSpan.Zero) is null)
            throw new RateLimitedException("Too many searches at once. Please pause for a moment.", null);
        var body = JsonSerializer.Serialize(new { query, variables });
        var text = await inflight.GetOrAdd(body, key => new Lazy<Task<string>>(() => SendSharedAsync(key))).Value.WaitAsync(ct);
        var document = JsonDocument.Parse(text);
        if (document.RootElement.TryGetProperty("errors", out _))
        {
            document.Dispose();
            throw new ProviderException("AniList could not load this catalog. Please try again later.");
        }
        return document;
    }

    private async Task<string> SendSharedAsync(string body)
    {
        try { return await SendAsync(body); }
        finally { inflight.TryRemove(body, out _); }
    }

    // Shared by every waiter, so it must not inherit one caller's cancellation.
    private async Task<string> SendAsync(string body)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        for (var attempt = 0; ; attempt++)
        {
            var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(8))
                ?? throw new RateLimitedException("AniList is busy. Please try again in a minute.", null);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, timeout.Token);
            try { return await http.TextAsync(Endpoint, timeout.Token, body); }
            catch (RateLimitedException ex) when (attempt == 0 && ex.RetryAfter is { } after && after <= TimeSpan.FromSeconds(10))
            {
                logger.LogWarning("AniList returned 429, retrying in {Seconds}s", after.TotalSeconds);
                budget.Exhaust(DateTime.UtcNow, after);
                await Task.Delay(after < TimeSpan.Zero ? TimeSpan.Zero : after, timeout.Token);
            }
            catch (RateLimitedException ex)
            {
                logger.LogWarning("AniList returned 429, pausing catalog requests for {Seconds}s", (ex.RetryAfter ?? TimeSpan.FromMinutes(1)).TotalSeconds);
                budget.Exhaust(DateTime.UtcNow, ex.RetryAfter ?? TimeSpan.FromMinutes(1));
                throw new RateLimitedException("AniList is busy. Please try again in a minute.", ex.RetryAfter);
            }
        }
    }

    public static Anime Parse(JsonElement node)
    {
        static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        static int? Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;
        var title = node.GetProperty("title");
        var aliases = new[] { Text(title, "romaji"), Text(title, "english"), Text(title, "native") }
            .Concat(node.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()))
            .OfType<string>().Where(s => s.Length > 0).Distinct().ToArray();
        var image = Text(node.GetProperty("coverImage"), "large");
        if (!Uri.TryCreate(image, UriKind.Absolute, out var imageUri) || imageUri.Scheme != "https" || !imageUri.Host.EndsWith(".anilist.co", StringComparison.Ordinal)) image = null;
        // An airing show has episodes up to the next one; a finished show reports its total.
        var next = node.TryGetProperty("nextAiringEpisode", out var airing) && airing.ValueKind == JsonValueKind.Object ? Number(airing, "episode") : null;
        var available = next is > 1 ? next - 1 : next is null ? Number(node, "episodes") : null;
        return new Anime(node.GetProperty("id").GetInt32(), Text(title, "english") ?? Text(title, "romaji") ?? "Unknown anime", aliases,
            Number(node.GetProperty("startDate"), "year"), AnimeGgSource.Clean(Text(node, "description") ?? ""), image,
            Number(node, "duration"), node.GetProperty("isAdult").GetBoolean(), node.GetProperty("genres").EnumerateArray().Select(g => g.GetString()!).ToArray(),
            Number(node, "idMal"), available);
    }
    public void Dispose() => cache.Dispose();
}

// A sliding window of request slots. Reserve hands out the next free slot and reports how
// long the caller must wait for it, or null without reserving when that exceeds maxWait.
// Slots are handed out in time order, so the list stays sorted.
public sealed class RateWindow(int limit, TimeSpan window)
{
    private readonly List<DateTime> slots = [];
    private DateTime blockedUntil;
    public TimeSpan? Reserve(DateTime now, TimeSpan maxWait)
    {
        lock (slots)
        {
            slots.RemoveAll(slot => slot <= now - window);
            var start = blockedUntil > now ? blockedUntil : now;
            if (slots.Count > 0 && slots[^1] > start) start = slots[^1];
            if (slots.Count >= limit && slots[^limit] + window > start) start = slots[^limit] + window;
            if (start - now > maxWait) return null;
            slots.Add(start);
            return start - now;
        }
    }
    public void Exhaust(DateTime now, TimeSpan duration)
    {
        lock (slots) { if (now + duration > blockedUntil) blockedUntil = now + duration; }
    }
}
