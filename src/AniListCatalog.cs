using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace AniFin;

public sealed class AniListCatalog(SafeHttp http) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private const string Fields = "id title { romaji english native } synonyms startDate { year } description(asHtml:false) coverImage { large } duration isAdult genres";
    private static readonly Uri Endpoint = new("https://graphql.anilist.co");
    private int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
    private TimeSpan Lifetime => TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440));
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
        var key = $"{Revision}/{PolicyKey}/{category}/{page}/{season}/{search?.Trim().ToLowerInvariant()}";
        if (cache.TryGetValue<CatalogPage>(key, out var found)) return found!;
        var order = category == "search" ? "SEARCH_MATCH" : category == "az" ? "TITLE_ROMAJI" : "POPULARITY_DESC";
        var seasonal = category == "season" ? $",season:{season.Season},seasonYear:{season.Year}" : "";
        var query = "query($page:Int" + (category == "search" ? ",$search:String" : "") + "){Page(page:$page,perPage:30){pageInfo{hasNextPage}media(type:ANIME" + Filters + ",sort:" + order + seasonal + (category == "search" ? ",search:$search" : "") + "){" + Fields + "}}}";
        using var document = await QueryAsync(query, new { page, search = search?.Trim() }, ct);
        var root = document.RootElement.GetProperty("data").GetProperty("Page");
        var items = root.GetProperty("media").EnumerateArray().Select(Parse).Where(IsAllowed).ToArray();
        foreach (var item in items) cache.Set($"anime/{Revision}/{item.Id}", item, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });
        var result = new CatalogPage(items, root.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });
        return result;
    }

    public async Task<Anime> GetAsync(int id, CancellationToken ct)
    {
        if (id <= 0) throw new ProviderException("Invalid anime identifier.");
        if (cache.TryGetValue<Anime>($"anime/{Revision}/{id}", out var found)) return found!;
        using var document = await QueryAsync("query($id:Int){Media(id:$id,type:ANIME){" + Fields + "}}", new { id }, ct);
        var node = document.RootElement.GetProperty("data").GetProperty("Media");
        if (node.ValueKind != JsonValueKind.Object) throw new ProviderException("This title is unavailable.");
        var anime = Parse(node);
        cache.Set($"anime/{Revision}/{id}", anime, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });
        return anime;
    }

    private async Task<JsonDocument> QueryAsync(string query, object variables, CancellationToken ct)
    {
        var body = await http.TextAsync(Endpoint, ct, JsonSerializer.Serialize(new { query, variables }));
        var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out _))
        {
            document.Dispose();
            throw new ProviderException("AniList could not load this catalog. Please try again later.");
        }
        return document;
    }
    private static Anime Parse(JsonElement node)
    {
        static string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var title = node.GetProperty("title");
        var aliases = new[] { Text(title, "romaji"), Text(title, "english"), Text(title, "native") }
            .Concat(node.GetProperty("synonyms").EnumerateArray().Select(s => s.GetString()))
            .OfType<string>().Where(s => s.Length > 0).Distinct().ToArray();
        var image = Text(node.GetProperty("coverImage"), "large");
        if (!Uri.TryCreate(image, UriKind.Absolute, out var imageUri) || imageUri.Scheme != "https" || !imageUri.Host.EndsWith(".anilist.co", StringComparison.Ordinal)) image = null;
        var year = node.GetProperty("startDate").GetProperty("year");
        var duration = node.GetProperty("duration");
        return new Anime(node.GetProperty("id").GetInt32(), Text(title, "english") ?? Text(title, "romaji") ?? "Unknown anime", aliases,
            year.ValueKind == JsonValueKind.Number ? year.GetInt32() : null, AnimeGgSource.Clean(Text(node, "description") ?? ""), image,
            duration.ValueKind == JsonValueKind.Number ? duration.GetInt32() : null,
            node.GetProperty("isAdult").GetBoolean(), node.GetProperty("genres").EnumerateArray().Select(g => g.GetString()!).ToArray());
    }
    public void Dispose() => cache.Dispose();
}
