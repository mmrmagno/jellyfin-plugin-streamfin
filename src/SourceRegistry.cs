namespace StreamFin;

public sealed record SourceMatches(string Id, string Name, IReadOnlyList<SourceTitle> Titles, string? Error = null);
public sealed record AnimePlayback(string Key, IReadOnlyList<ResolvedVideo> Videos);

public sealed class SourceRegistry(IEnumerable<IAnimeSource> providers, VideoHealth? health = null) : IDisposable
{
    // Which titles a site has changes rarely; its episode list changes once a week while a show airs.
    private readonly FreshCache<SourceMatches> matches = new(m => m.Error is null && m.Titles.Count > 0);
    private readonly FreshCache<IReadOnlyList<SourceEpisode>> episodes = new(e => e.Count > 0);
    private readonly VideoHealth health = health ?? new VideoHealth();
    public VideoHealth Health => health;
    private static int Revision => Plugin.Instance?.Configuration.CacheRevision ?? 0;
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
        => EpisodesAsync(Get(key, out var id), id, anime, ct);
    // A site that is down answers from what it said before, or at once that it is down; nobody waits on it.
    private Task<IReadOnlyList<SourceEpisode>> EpisodesAsync(IAnimeSource source, string id, Anime anime, CancellationToken ct)
    {
        var key = $"{Revision}/{source.Id}/{id}/{anime.Id}";
        if (!health.Usable(source.Id))
            return episodes.TryPeek(key, out var known) ? Task.FromResult(known) : throw new ProviderException(health.Describe(source.Id) ?? source.Name + " is not answering right now.");
        return episodes.GetAsync(key, TimeSpan.FromMinutes(30), token => source.EpisodesAsync(id, anime, token), ct);
    }
    private Task<SourceMatches> MatchCachedAsync(IAnimeSource source, Anime anime, CancellationToken ct)
    {
        var key = $"{Revision}/{source.Id}/{anime.Id}";
        if (!health.Usable(source.Id))
            return Task.FromResult(matches.TryPeek(key, out var known) ? known : new SourceMatches(source.Id, source.Name, [], health.Describe(source.Id)));
        return matches.GetAsync(key, TimeSpan.FromHours(6), token => MatchAsync(source, anime, token), ct);
    }
    public Task<IReadOnlyList<ResolvedVideo>> ResolveAsync(string key, CancellationToken ct)
        => Get(key, out var id).ResolveAsync(id, ct);
    public string Name(string key) => Get(key, out _).Name;

    public async Task<IReadOnlyList<SourceMatches>> SearchAsync(Anime anime, CancellationToken ct)
        => await Task.WhenAll(ordered.Where(p => IsEnabled(p.Id)).Select(p => MatchCachedAsync(p, anime, ct)));

    // The chosen site first. When it cannot play the episode, the same episode from another site: only when the chosen
    // episode belongs to that site's one exact match for the title (known from the listing, or asked), it has a real
    // number, and the other site has exactly this title (and year) with exactly one episode of that number. So a
    // special, another season or a wrong show never plays in its place.
    public async Task<AnimePlayback> ResolveWithFallbackAsync(Anime anime, decimal? number, string preferred, CancellationToken ct,
        Configuration? settings = null, bool? listedExact = null)
    {
        var config = settings ?? Plugin.Instance?.Configuration;
        var (chosenSite, chosenId) = Split(preferred);
        if (!IsEnabled(chosenSite, config) || !sources.TryGetValue(chosenSite, out var chosen))
            throw new ProviderException("This provider is disabled or unavailable. Choose another provider under the title.");
        var others = health.Fastest(ordered.Where(p => p != chosen && IsEnabled(p.Id, config) && health.Usable(p.Id)), p => p.Id).ToArray();
        var fallback = number > 0 && config?.AnimeProviderFallback != false && others.Length > 0 && listedExact != false;
        var failures = new List<string>();
        // With somewhere to go, a site that hangs gives way after 30 s; without, it gets the time it needs, as before.
        // A site known to be down is not waited on when another can play the episode.
        if (fallback && !health.Usable(chosen.Id)) failures.Add(health.Describe(chosen.Id) ?? chosen.Name + " is not answering right now.");
        else
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (fallback) attempt.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var videos = await chosen.ResolveAsync(chosenId, attempt.Token);
                if (videos.Count > 0) return new(preferred, videos);
                failures.Add(chosen.Name + ": no playable video was returned.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex))
            {
                health.Failed(chosen.Id, ex, ct);
                if (!fallback) throw;
                failures.Add(chosen.Name + ": " + (ex is ProviderException ? ex.Message : "not answering"));
            }
        }
        if (!fallback) throw new ProviderException("This episode cannot be played right now. " + string.Join(" ", failures));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(40));
        if (listedExact != true && !await IsExactAsync(chosen, chosenId, anime, budget.Token))
            throw new ProviderException("This episode cannot be played right now. " + string.Join(" ", failures));
        foreach (var source in others)
        {
            if (budget.IsCancellationRequested) break;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
            attempt.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var exact = (await MatchCachedAsync(source, anime, attempt.Token)).Titles.Where(t => IsExact(t, anime)).ToArray();
                if (exact.Length != 1) continue;
                var same = (await EpisodesAsync(source, exact[0].Id, anime, attempt.Token)).Where(e => e.Number == number).ToArray();
                if (same.Length != 1) continue;
                var videos = await source.ResolveAsync(same[0].Id, attempt.Token);
                if (videos.Count == 0) throw new ProviderException("No playable video was returned.");
                return new(Key(source.Id, same[0].Id), videos);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex))
            {
                health.Failed(source.Id, ex, budget.Token);
                failures.Add(source.Name + ": " + (ex is ProviderException ? ex.Message : "not answering"));
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new ProviderException("This episode cannot be played right now. " + string.Join(" ", failures));
    }

    private static bool IsSiteFailure(Exception ex) => ex is ProviderException or HttpRequestException or OperationCanceledException
        or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException;

    // A site's title is this show when its name is the show's title or one of its other names, and its year (if the
    // site gives one) is the show's.
    public static bool IsExact(SourceTitle title, Anime anime) =>
        anime.Aliases.Prepend(anime.Title).Any(name => AnimeGgSource.Normalize(name) == AnimeGgSource.Normalize(title.Title)) && (!title.Year.HasValue || title.Year == anime.Year);

    // Whether the episode belongs to the site's one exact match for the title, asked of the site. A site that does not
    // answer in time proves nothing, so then there is no fallback.
    private async Task<bool> IsExactAsync(IAnimeSource site, string episodeId, Anime anime, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var exact = (await MatchCachedAsync(site, anime, limit.Token)).Titles.Where(t => IsExact(t, anime)).ToArray();
            return exact.Length == 1 && (await EpisodesAsync(site, exact[0].Id, anime, limit.Token)).Count(e => e.Id == episodeId) == 1;
        }
        catch (Exception ex) when (IsSiteFailure(ex)) { return false; }
    }

    public void Dispose()
    {
        matches.Dispose();
        episodes.Dispose();
    }

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
            var exact = unique.Where(c => IsExact(c, anime)).ToArray();
            return new(source.Id, source.Name, exact.Length == 1 ? exact : unique);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException)
        {
            return new(source.Id, source.Name, [], "Temporarily unavailable. Try the other provider or refresh the catalog cache.");
        }
    }
}
