using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public enum FilmKind { Movie, Tv }
public sealed record Film(int Id, FilmKind Kind, string Title, int? Year, string Overview, string? Image, int? Runtime, bool IsAdult,
    double? Vote = null, int[]? GenreIds = null, DateTime? Premiere = null, string? Backdrop = null);
public sealed record FilmPerson(string Name, string? Role, string? Photo);
// What TMDB's details call adds to a title: fetched once a day per title, by the warm task or in the background.
public sealed record FilmDetails(string? ImdbId, string? Certification, string[] Studios, FilmPerson[] Cast, FilmPerson[] Directors,
    string? Backdrop, string? Logo, string[] Genres);
public sealed record FilmPage(IReadOnlyList<Film> Items, bool HasNext);
public sealed record FilmSeason(int Number, string Name, int Episodes, string? Image);
public sealed record FilmEpisode(int Season, int Number, string Name, string Overview, string? Image, int? Runtime, DateTime? Aired);

public sealed class TmdbCatalog(SafeHttp http, ILogger<TmdbCatalog>? logger = null) : IDisposable
{
    private readonly MemoryCache cache = CacheRegistry.Track("Film lookups", new(new MemoryCacheOptions { SizeLimit = 4000 }));
    private readonly RateWindow budget = new(40, TimeSpan.FromSeconds(10));
    private readonly FreshCache<object> fresh = new(_ => true, 2000, failed: ex => logger?.LogInformation("StreamFin films: refreshing a page in the background failed ({ErrorType})", ex.GetType().Name), name: "Films");
    private static readonly Uri Endpoint = new("https://api.themoviedb.org/3/");
    private const string Images = "https://image.tmdb.org/t/p/w500";
    // Wide pictures are shown large, logos and faces small.
    private const string Backdrops = "https://image.tmdb.org/t/p/w1280";
    private const string Logos = "https://image.tmdb.org/t/p/w500";
    private const string Faces = "https://image.tmdb.org/t/p/w185";
    // ".2" since titles carry rating, genres, premiere and backdrop: titles cached without them are fetched again.
    private string Revision => (Plugin.Instance?.Configuration.CacheRevision ?? 0).ToString(CultureInfo.InvariantCulture) + ".2";
    // Movie and TV genre ids share one table: the ids that differ never mean two different genres.
    private static readonly IReadOnlyDictionary<int, string> GenreNames = new Dictionary<int, string>
    {
        [28] = "Action", [12] = "Adventure", [16] = "Animation", [35] = "Comedy", [80] = "Crime", [99] = "Documentary",
        [18] = "Drama", [10751] = "Family", [14] = "Fantasy", [36] = "History", [27] = "Horror", [10402] = "Music",
        [9648] = "Mystery", [10749] = "Romance", [878] = "Science Fiction", [10770] = "TV Movie", [53] = "Thriller",
        [10752] = "War", [37] = "Western", [10759] = "Action & Adventure", [10762] = "Kids", [10763] = "News",
        [10764] = "Reality", [10765] = "Sci-Fi & Fantasy", [10766] = "Soap", [10767] = "Talk", [10768] = "War & Politics"
    };
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
        return CachedAsync($"{category}/{page}/{search?.Trim().ToLowerInvariant()}", category == "search" ? TimeSpan.FromMinutes(30) : Lifetime, async token =>
        {
            using var document = await QueryAsync($"{path}include_adult=false&language=en-US&page={page}", token);
            var root = document.RootElement;
            var fallback = category == "movies" ? FilmKind.Movie : FilmKind.Tv;
            var items = root.GetProperty("results").EnumerateArray()
                .Select(node => Parse(node, category is "movies" or "tv" ? fallback : null)).OfType<Film>().Where(f => !f.IsAdult).ToArray();
            foreach (var film in items) Store($"{film.Kind}/{film.Id}", film, Lifetime);
            return new FilmPage(items, root.TryGetProperty("total_pages", out var total) && total.TryGetInt32(out var pages) && page < pages);
        }, ct);
    }

    public Task<Film> GetAsync(FilmKind kind, int id, CancellationToken ct) => id <= 0 ? throw new ProviderException("Invalid title identifier.")
        : CachedAsync($"{kind}/{id}", Lifetime, async token =>
        {
            using var document = await QueryAsync($"{(kind == FilmKind.Movie ? "movie" : "tv")}/{id}?language=en-US", token);
            return Parse(document.RootElement, kind) ?? throw new ProviderException("This title is unavailable.");
        }, ct);

    public Task<IReadOnlyList<FilmSeason>> SeasonsAsync(int id, CancellationToken ct) => CachedAsync<IReadOnlyList<FilmSeason>>($"seasons/{id}", Lifetime, async token =>
    {
        using var document = await QueryAsync($"tv/{id}?language=en-US", token);
        // Season 0 holds specials, which streaming sites rarely carry under the same numbering.
        return document.RootElement.GetProperty("seasons").EnumerateArray()
            .Select(s => new FilmSeason(s.GetProperty("season_number").GetInt32(), Text(s, "name") ?? "Season", Number(s, "episode_count") ?? 0, Image(s, "poster_path")))
            .Where(s => s.Number > 0 && s.Episodes > 0).OrderBy(s => s.Number).ToArray();
    }, ct);

    public Task<IReadOnlyList<FilmEpisode>> EpisodesAsync(int id, int season, CancellationToken ct) => season is < 1 or > 500
        ? throw new ProviderException("Invalid season.")
        : CachedAsync<IReadOnlyList<FilmEpisode>>($"season/{id}/{season}", Lifetime, async token =>
        {
            using var document = await QueryAsync($"tv/{id}/season/{season}?language=en-US", token);
            // Unaired episodes are listed in advance; nothing can stream them yet.
            return document.RootElement.GetProperty("episodes").EnumerateArray()
                .Select(e => new FilmEpisode(season, e.GetProperty("episode_number").GetInt32(), Text(e, "name") ?? "Episode", Text(e, "overview") ?? "",
                    Image(e, "still_path"), Number(e, "runtime"),
                    DateTime.TryParse(Text(e, "air_date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var aired) ? aired : null))
                .Where(e => e.Aired is null || e.Aired <= DateTime.UtcNow.AddDays(1)).OrderBy(e => e.Number).ToArray();
        }, ct);

    // Cast, crew, studios, age rating, IMDb id and pictures: one call per title, kept a day.
    public Task<FilmDetails> DetailsAsync(Film film, CancellationToken ct) => CachedAsync($"details/{film.Kind}/{film.Id}", TimeSpan.FromHours(24), async token =>
    {
        var path = film.Kind == FilmKind.Movie
            ? $"movie/{film.Id}?language=en-US&append_to_response=external_ids,credits,release_dates,images&include_image_language=en,null"
            : $"tv/{film.Id}?language=en-US&append_to_response=external_ids,aggregate_credits,content_ratings,images&include_image_language=en,null";
        using var document = await QueryAsync(path, token);
        return Details(document.RootElement, film.Kind);
    }, ct);

    // Fetches the details of titles a listing showed, one at a time and only while TMDB's budget has room, so the next
    // listing can show cast and studios. One loop at a time server-wide; titles already known are skipped.
    private int prefetching;
    public void PrefetchDetails(IEnumerable<Film> films)
    {
        var missing = films.Where(f => KnownDetails(f) is null).DistinctBy(f => (f.Kind, f.Id)).Take(100).ToList();
        if (missing.Count == 0 || Interlocked.Exchange(ref prefetching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                foreach (var film in missing)
                {
                    await SpareAsync(limit.Token);
                    try { await DetailsAsync(film, limit.Token); }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException && !limit.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) { }
            finally { Interlocked.Exchange(ref prefetching, 0); }
        }, CancellationToken.None);
    }

    // Background work waits until TMDB's budget has room to spare, so browsing never runs short of calls.
    public async Task SpareAsync(CancellationToken ct)
    {
        while (budget.Free(DateTime.UtcNow) < 15) await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    // Details only if they are already known, for listings that must not wait on TMDB.
    public FilmDetails? KnownDetails(Film film) => cache.TryGetValue<FilmDetails>($"{Revision}/details/{film.Kind}/{film.Id}", out var details) ? details
        : cache.TryGetValue("stale/details/" + film.Kind + "/" + film.Id, out details) ? details : null;

    public static IReadOnlyList<string> Genres(Film film) => (film.GenreIds ?? []).Select(id => GenreNames.GetValueOrDefault(id)).OfType<string>().Distinct().ToArray();
    // TMDB scores 0 to 10, as Jellyfin does; 0 means not rated yet.
    public static float? Rating(Film film) => film.Vote is > 0 and <= 10 ? (float)Math.Round(film.Vote.Value, 1) : null;

    public static FilmDetails Details(JsonElement root, FilmKind kind)
    {
        var imdb = root.TryGetProperty("external_ids", out var external) && external.ValueKind == JsonValueKind.Object ? Text(external, "imdb_id") : null;
        var credits = root.TryGetProperty(kind == FilmKind.Movie ? "credits" : "aggregate_credits", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        FilmPerson[] People(string group, Func<JsonElement, bool> keep, Func<JsonElement, string?> role, int take) =>
            credits.ValueKind == JsonValueKind.Object && credits.TryGetProperty(group, out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(keep).Select(p => (Name: Text(p, "name"), Role: role(p), Photo: ImageAt(Faces, p, "profile_path")))
                    .Where(p => p.Name is not null).DistinctBy(p => p.Name).Take(take).Select(p => new FilmPerson(p.Name!, p.Role, p.Photo)).ToArray()
                : [];
        // A show's credits list each actor's roles; the first one is enough.
        static string? Character(JsonElement p) => Text(p, "character")
            ?? (p.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array && roles.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } first ? Text(first, "character") : null);
        var cast = People("cast", _ => true, Character, 12);
        var directors = kind == FilmKind.Movie
            ? People("crew", p => Text(p, "job") == "Director", _ => "Director", 4)
            : root.TryGetProperty("created_by", out var creators) && creators.ValueKind == JsonValueKind.Array
                ? creators.EnumerateArray().Select(p => Text(p, "name") is { } name ? new FilmPerson(name, "Creator", ImageAt(Faces, p, "profile_path")) : null).OfType<FilmPerson>().Take(4).ToArray()
                : [];
        string[] Names(string property) => root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(n => Text(n, "name")).OfType<string>().Distinct(StringComparer.Ordinal).Take(6).ToArray() : [];
        return new FilmDetails(imdb is { Length: > 2 } && imdb.StartsWith("tt", StringComparison.Ordinal) ? imdb : null, Certification(root, kind),
            Names(kind == FilmKind.Movie ? "production_companies" : "networks"), cast, directors,
            ImageAt(Backdrops, root, "backdrop_path"), Logo(root), Names("genres"));
    }

    // The US age rating: a movie's first theatrical certification, a show's content rating.
    private static string? Certification(JsonElement root, FilmKind kind)
    {
        if (!root.TryGetProperty(kind == FilmKind.Movie ? "release_dates" : "content_ratings", out var container) || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var country in results.EnumerateArray())
        {
            if (Text(country, "iso_3166_1") != "US") continue;
            if (kind == FilmKind.Movie)
                return country.TryGetProperty("release_dates", out var dates) && dates.ValueKind == JsonValueKind.Array
                    ? dates.EnumerateArray().Select(d => Text(d, "certification")).FirstOrDefault(v => v is { Length: > 0 } and not "NR") : null;
            return Text(country, "rating") is { Length: > 0 } rating and not "NR" ? rating : null;
        }
        return null;
    }

    // Title art: an English logo first, then one without text, the best voted.
    private static string? Logo(JsonElement root) =>
        root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Object && images.TryGetProperty("logos", out var logos) && logos.ValueKind == JsonValueKind.Array
            ? logos.EnumerateArray().Select(l => (Url: ImageAt(Logos, l, "file_path"), English: Text(l, "iso_639_1") == "en",
                    Vote: l.TryGetProperty("vote_average", out var v) && v.TryGetDouble(out var score) ? score : 0))
                .Where(l => l.Url is not null).OrderByDescending(l => l.English).ThenByDescending(l => l.Vote).Select(l => l.Url).FirstOrDefault()
            : null;

    // One small call straight to TMDB, past every cache, for the settings page's test.
    public async Task ProbeAsync(CancellationToken ct)
    {
        using var document = await QueryAsync("configuration", ct);
    }

    private async Task<T> CachedAsync<T>(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        if (cache.TryGetValue<T>($"{Revision}/{key}", out var found)) return found!;
        try
        {
            // Seen before, it answers at once and refreshes behind, while TMDB's budget has room.
            return (T)await fresh.GetAsync($"{Revision}/{key}", lifetime, async token =>
            {
                var value = await load(token);
                Store(key, value, lifetime);
                return (object)value!;
            }, ct, budget.Free(DateTime.UtcNow) > 10);
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

    // TMDB text is plain; angle brackets are dropped so no client can mistake it for markup.
    private static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        && value.GetString()?.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal) is { Length: > 0 } s ? s : null;
    private static int? Number(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    private static string? Image(JsonElement e, string key) => ImageAt(Images, e, key);
    private static string? ImageAt(string size, JsonElement e, string key) => Text(e, key) is { } path && Regex.IsMatch(path, @"\A/[A-Za-z0-9_-]+\.(?:jpg|png|webp)\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? size + path : null;

    public static Film? Parse(JsonElement node, FilmKind? kind)
    {
        kind ??= Text(node, "media_type") switch { "movie" => FilmKind.Movie, "tv" => FilmKind.Tv, _ => null };
        if (kind is not { } k || Number(node, "id") is not { } id) return null;
        var title = Text(node, k == FilmKind.Movie ? "title" : "name");
        if (title is null) return null;
        var date = Text(node, k == FilmKind.Movie ? "release_date" : "first_air_date");
        int? year = date is { Length: >= 4 } && int.TryParse(date[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
        int? runtime = Number(node, "runtime") ?? (node.TryGetProperty("episode_run_time", out var runs) && runs.ValueKind == JsonValueKind.Array
            && runs.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Number } first && first.TryGetInt32(out var run) ? run : null);
        // A list gives genre ids, a single title its genres with ids.
        var genres = node.TryGetProperty("genre_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(g => g.ValueKind == JsonValueKind.Number && g.TryGetInt32(out var n) ? n : 0)
            : node.TryGetProperty("genres", out var named) && named.ValueKind == JsonValueKind.Array ? named.EnumerateArray().Select(g => Number(g, "id") ?? 0) : [];
        double? vote = node.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var score) ? score : null;
        DateTime? premiere = DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var released) ? released : null;
        return new Film(id, k, title, year, Text(node, "overview") ?? "", Image(node, "poster_path"), runtime,
            node.TryGetProperty("adult", out var adult) && adult.ValueKind == JsonValueKind.True,
            vote, genres.Where(g => g > 0).Distinct().ToArray(), premiere, ImageAt(Backdrops, node, "backdrop_path"));
    }
    public void Dispose()
    {
        fresh.Dispose();
        cache.Dispose();
    }
}
