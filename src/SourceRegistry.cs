namespace StreamFin;

public sealed record SourceMatches(string Id, string Name, IReadOnlyList<SourceTitle> Titles, string? Error = null);

public sealed class SourceRegistry(IEnumerable<IAnimeSource> providers)
{
    // Shown in this order under every title: the most reliable source first.
    public static readonly string[] Order = ["zoko", "animegg", "animeheaven"];
    private readonly IAnimeSource[] ordered = providers.OrderBy(p => Array.IndexOf(Order, p.Id) is var i and >= 0 ? i : Order.Length).ToArray();
    private readonly IReadOnlyDictionary<string, IAnimeSource> sources = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
    public static bool IsEnabled(string id) => IsEnabled(id, Plugin.Instance?.Configuration);
    public static bool IsEnabled(string id, Configuration? config) => config is { Enabled: true } && id switch
    {
        "zoko" => config.ZokoEnabled,
        "animegg" => config.AnimeGgEnabled,
        "animeheaven" => config.AnimeHeavenEnabled,
        _ => false
    };
    public static bool AnyEnabled => Order.Any(IsEnabled);
    public static string CacheKey => string.Join('-', Order.Select(id => IsEnabled(id)));

    // Keep legacy AnimeGG keys byte-for-byte: changing them would orphan saved episode progress.
    public static string Key(string provider, string id) => provider == "animegg" ? id : provider + ":" + id;
    public static (string Provider, string Id) Split(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? ("animegg", key) : (key[..separator], key[(separator + 1)..]);
    }
    public IAnimeSource Get(string key, out string id)
    {
        var parsed = Split(key);
        id = parsed.Id;
        if (!IsEnabled(parsed.Provider) || !sources.TryGetValue(parsed.Provider, out var source))
            throw new ProviderException("This provider is disabled or unavailable. Choose another provider under the title.");
        return source;
    }
    public Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(string key, Anime anime, CancellationToken ct)
        => Get(key, out var id).EpisodesAsync(id, anime, ct);
    public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string key, CancellationToken ct)
        => Get(key, out var id).ResolveAsync(id, ct);
    public string Name(string key) => Get(key, out _).Name;

    public async Task<IReadOnlyList<SourceMatches>> SearchAsync(Anime anime, CancellationToken ct)
        => await Task.WhenAll(ordered.Where(p => IsEnabled(p.Id)).Select(p => MatchAsync(p, anime, ct)));

    // Each provider has its own budget. One failed site must not hide the working source.
    public static async Task<SourceMatches> MatchAsync(IAnimeSource source, Anime anime, CancellationToken ct)
    {
        if (source is IDirectAnimeSource direct)
            return direct.Direct(anime) is { } title ? new(source.Id, source.Name, [title])
                : new(source.Id, source.Name, [], "No MyAnimeList entry for this title.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var candidates = new List<SourceTitle>();
            foreach (var title in anime.Aliases.Prepend(anime.Title).Distinct().Take(3))
            {
                var response = await source.SearchAsync(title, timeout.Token);
                candidates.AddRange(response);
                if (candidates.Any(c => AnimeGgSource.Normalize(c.Title) == AnimeGgSource.Normalize(title))) break;
            }
            var names = anime.Aliases.Prepend(anime.Title).Select(AnimeGgSource.Normalize).ToHashSet();
            // Some sites put unrelated promoted titles on empty/error search pages.
            // Never present one of those as a match for the requested catalog title.
            var unique = candidates.DistinctBy(c => c.Id).Where(c => names.Any(name =>
            {
                var candidate = AnimeGgSource.Normalize(c.Title);
                return candidate == name || (Math.Min(name.Length, candidate.Length) >= 4
                    && (candidate.Contains(name, StringComparison.Ordinal) || name.Contains(candidate, StringComparison.Ordinal)));
            })).ToArray();
            var exact = unique.Where(c => names.Contains(AnimeGgSource.Normalize(c.Title)) && (!c.Year.HasValue || c.Year == anime.Year)).ToArray();
            return new(source.Id, source.Name, exact.Length == 1 ? exact : unique);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return new(source.Id, source.Name, [], "Temporarily unavailable. Try the other provider or refresh the catalog cache.");
        }
    }
}
