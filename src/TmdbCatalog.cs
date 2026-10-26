using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public enum FilmKind { Movie, Tv }
public sealed record Film(int Id, FilmKind Kind, string Title, int? Year, string Overview, string? Image, int? Runtime, bool IsAdult);
public sealed record FilmPage(IReadOnlyList<Film> Items, bool HasNext);
public sealed record FilmSeason(int Number, string Name, int Episodes, string? Image);
public sealed record FilmEpisode(int Season, int Number, string Name, string Overview, string? Image, int? Runtime, DateTime? Aired);

// TMDB metadata for the Movies & TV channel. The administrator supplies the free API key;
// both the short v3 key and the long read access token work.
public sealed class TmdbCatalog(SafeHttp http) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 4000 });
    private readonly RateWindow budget = new(40, TimeSpan.FromSeconds(10));
    private static readonly Uri Endpoint = new("https://api.themoviedb.org/3/");
    private const string Images = "https://image.tmdb.org/t/p/w500";
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
    private static string Key => Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? "";
    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440));
    public static bool HasKey => IsValidKey(Key);
    public static bool IsValidKey(string key) => Regex.IsMatch(key, @"\A(?:[a-f0-9]{32}|eyJ[A-Za-z0-9_.-]{20,1000})\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    public Task<FilmPage> BrowseAsync(string category, int page, CancellationToken ct, string? search = null)
    {
        if (page is < 1 or > 500) throw new ProviderException("Invalid catalog page.");
        if (category == "search" && (string.IsNullOrWhiteSpace(search) || search.Trim().Length is < 2 or > 100)) return Task.FromResult(new FilmPage([], false));
        var path = category switch
        {
            "movies" => "movie/popular?",
            "tv" => "tv/popular?",
            "trending" => "trending/all/week?",
            "search" => "search/multi?query=" + Uri.EscapeDataString(search!.Trim()) + "&",
            _ => throw new ProviderException("Invalid catalog page.")
        };
        return CachedAsync($"{category}/{page}/{search?.Trim().ToLowerInvariant()}", category == "search" ? TimeSpan.FromMinutes(30) : Lifetime, async () =>
        {
            using var document = await QueryAsync($"{path}include_adult=false&language=en-US&page={page}", ct);
            var root = document.RootElement;
            var fallback = category == "movies" ? FilmKind.Movie : FilmKind.Tv;
            var items = root.GetProperty("results").EnumerateArray()
                .Select(node => Parse(node, category is "movies" or "tv" ? fallback : null)).OfType<Film>().Where(f => !f.IsAdult).ToArray();
            foreach (var film in items) Store($"{film.Kind}/{film.Id}", film, Lifetime);
            return new FilmPage(items, root.TryGetProperty("total_pages", out var total) && total.TryGetInt32(out var pages) && page < pages);
        });
    }

    public Task<Film> GetAsync(FilmKind kind, int id, CancellationToken ct) => id <= 0 ? throw new ProviderException("Invalid title identifier.")
        : CachedAsync($"{kind}/{id}", Lifetime, async () =>
        {
            using var document = await QueryAsync($"{(kind == FilmKind.Movie ? "movie" : "tv")}/{id}?language=en-US", ct);
            return Parse(document.RootElement, kind) ?? throw new ProviderException("This title is unavailable.");
        });

    public Task<IReadOnlyList<FilmSeason>> SeasonsAsync(int id, CancellationToken ct) => CachedAsync<IReadOnlyList<FilmSeason>>($"seasons/{id}", Lifetime, async () =>
    {
        using var document = await QueryAsync($"tv/{id}?language=en-US", ct);
        // Season 0 holds specials, which streaming sites rarely carry under the same numbering.
        return document.RootElement.GetProperty("seasons").EnumerateArray()
            .Select(s => new FilmSeason(s.GetProperty("season_number").GetInt32(), Text(s, "name") ?? "Season", Number(s, "episode_count") ?? 0, Image(s, "poster_path")))
            .Where(s => s.Number > 0 && s.Episodes > 0).OrderBy(s => s.Number).ToArray();
    });

    public Task<IReadOnlyList<FilmEpisode>> EpisodesAsync(int id, int season, CancellationToken ct) => season is < 1 or > 500
        ? throw new ProviderException("Invalid season.")
        : CachedAsync<IReadOnlyList<FilmEpisode>>($"season/{id}/{season}", Lifetime, async () =>
        {
            using var document = await QueryAsync($"tv/{id}/season/{season}?language=en-US", ct);
            // Unaired episodes are listed in advance; nothing can stream them yet.
            return document.RootElement.GetProperty("episodes").EnumerateArray()
                .Select(e => new FilmEpisode(season, e.GetProperty("episode_number").GetInt32(), Text(e, "name") ?? "Episode", Text(e, "overview") ?? "",
                    Image(e, "still_path"), Number(e, "runtime"),
                    DateTime.TryParse(Text(e, "air_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var aired) ? aired : null))
                .Where(e => e.Aired is null || e.Aired <= DateTime.UtcNow.AddDays(1)).OrderBy(e => e.Number).ToArray();
        });

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<Task<T>> load)
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        try
        {
            var value = await load();
            Store(key, value, lifetime);
            return value;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException
            && cache.TryGetValue<T>("stale/" + key, out var stale)) { return stale!; }
    }
    private void Store<T>(string key, T value, TimeSpan lifetime)
    {
        cache.Set($"{Revision}/{key}", value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        cache.Set("stale/" + key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
    }

    private async Task<JsonDocument> QueryAsync(string path, CancellationToken ct)
    {
        var key = Key;
        if (!IsValidKey(key)) throw new ProviderException("Add a TMDB API key under Dashboard → Plugins → StreamFin to use Movies & TV.");
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("TMDB is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var uri = new Uri(Endpoint, bearer ? path : path + (path.Contains('?') ? "&" : "?") + "api_key=" + key);
        string text;
        try { text = await http.TextAsync(uri, ct, headers: bearer ? new Dictionary<string, string> { ["Authorization"] = "Bearer " + key } : null); }
        catch (ProviderException ex) when (ex.Message.Contains("HTTP 401", StringComparison.Ordinal))
        { throw new ProviderException("TMDB rejected the API key. Check it under Dashboard → Plugins → StreamFin."); }
        return JsonDocument.Parse(text);
    }

    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;
    private static int? Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    private static string? Image(JsonElement e, string key) => Text(e, key) is { } path && Regex.IsMatch(path, @"\A/[A-Za-z0-9_-]+\.(?:jpg|png|webp)\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? Images + path : null;

    public static Film? Parse(JsonElement node, FilmKind? kind)
    {
        kind ??= Text(node, "media_type") switch { "movie" => FilmKind.Movie, "tv" => FilmKind.Tv, _ => null };
        if (kind is not { } k || Number(node, "id") is not { } id) return null;
        var title = Text(node, k == FilmKind.Movie ? "title" : "name");
        if (title is null) return null;
        var date = Text(node, k == FilmKind.Movie ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        int? runtime = Number(node, "runtime") ?? (node.TryGetProperty("episode_run_time", out var runs) && runs.ValueKind == JsonValueKind.Array
            && runs.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } first ? first.GetInt32() : null);
        return new Film(id, k, title, year, Text(node, "overview") ?? "", Image(node, "poster_path"), runtime,
            node.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True);
    }
    public void Dispose() => cache.Dispose();
}
