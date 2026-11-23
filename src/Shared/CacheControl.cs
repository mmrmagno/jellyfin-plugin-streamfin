using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Every catalog and source cache registers here by name where it is created. Status counts their entries; Clear
// empties them all. Weak references: a cache that is disposed or collected simply drops out of the list.
public static class CacheRegistry
{
    private static readonly object Gate = new();
    private static readonly List<(string Name, WeakReference<MemoryCache> Cache)> Entries = [];

    // Returns the cache it was given, so the field that creates it can register it in the same line.
    public static MemoryCache Track(string? name, MemoryCache cache)
    {
        if (name is not null) lock (Gate) Entries.Add((name, new WeakReference<MemoryCache>(cache)));
        return cache;
    }

    private static List<(string Name, MemoryCache Cache)> Live()
    {
        lock (Gate)
        {
            Entries.RemoveAll(e => !e.Cache.TryGetTarget(out _));
            return Entries.Select(e => e.Cache.TryGetTarget(out var cache) ? (e.Name, cache) : default).Where(e => e.cache is not null).ToList()!;
        }
    }

    public static IReadOnlyList<CacheEntryStatus> Status() => Live().Select(e =>
    {
        try { return new CacheEntryStatus(e.Name, e.Cache.Count); }
        catch (ObjectDisposedException) { return new CacheEntryStatus(e.Name, 0); }
    }).ToArray();

    public static void ClearAll()
    {
        foreach (var (_, cache) in Live())
        {
            try { cache.Compact(1.0); }
            catch (ObjectDisposedException) { }
        }
    }
}

public sealed record CacheEntryStatus(string Name, int Entries);

// Warms the first pages the Anime and Movies & TV channels open to, so the first browse after a restart is instant.
public sealed class CatalogWarmer(AniListCatalog anime, TmdbCatalog films, ILogger<CatalogWarmer> logger)
{
    private static readonly string[] AnimePages = ["popular", "season", "az"];
    private static readonly string[] FilmCategories = ["movies", "tv", "trending"];

    public async Task RunAsync(CancellationToken ct)
    {
        var steps = new List<Func<CancellationToken, Task>>();
        if (SourceRegistry.AnyEnabled)
            foreach (var category in AnimePages)
                foreach (var page in new[] { 1, 2, 3, 4 })
                    steps.Add(token => anime.BrowseAsync(category, page, token));
        if (FilmChannel.IsAvailable)
            foreach (var category in FilmCategories)
                foreach (var page in new[] { 1, 2, 3 })
                    steps.Add(async token =>
                    {
                        // Cast, studios and pictures of the warmed titles too, so their pages show them from the start.
                        foreach (var film in (await films.BrowseAsync(category, page, token)).Items)
                            try { if (films.KnownDetails(film) is null) { await films.SpareAsync(token); await films.DetailsAsync(film, token); } }
                            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException) { }
                    });
        // One request at a time: each provider keeps its own rate limit, and a failed page only skips itself.
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            try { await step(ct); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                logger.LogInformation("StreamFin warm-up skipped a page ({ErrorType})", ex.GetType().Name);
            }
        }
    }
}

public sealed class CatalogWarmTask(CatalogWarmer warmer) : IScheduledTask
{
    public string Name => "Warm StreamFin catalogs";
    public string Key => "StreamFinCatalogWarm";
    public string Description => "Loads the first pages of StreamFin's anime and movie/TV catalogs into its cache, one request at a time, at startup and every six hours.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await warmer.RunAsync(cancellationToken);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks }
    ];
}

public sealed class CacheClearTask : IScheduledTask
{
    public string Name => "Clear StreamFin caches";
    public string Key => "StreamFinCacheClear";
    public string Description => "Empties StreamFin's catalog and source caches and refreshes Jellyfin's saved listings. Runs only when started by hand.";
    public string Category => "StreamFin";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        CacheControl.Clear();
        progress.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}

public static class CacheControl
{
    // Changing CacheRevision makes Jellyfin rebuild its saved listings, so cleared pages are not put back from its copy.
    public static void Clear()
    {
        CacheRegistry.ClearAll();
        if (Plugin.Instance is not { } plugin) return;
        plugin.Configuration.CacheRevision++;
        plugin.SaveConfiguration();
    }
}

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/Cache")]
public sealed class CacheController(CatalogWarmer warmer) : ControllerBase
{
    [HttpGet]
    public ActionResult<IReadOnlyList<CacheEntryStatus>> Status() => CacheRegistry.Status().ToArray();

    [HttpPost("Clear")]
    public ActionResult Clear()
    {
        CacheControl.Clear();
        return NoContent();
    }

    [HttpPost("Warm")]
    public async Task<ActionResult> Warm(CancellationToken ct)
    {
        await warmer.RunAsync(ct);
        return NoContent();
    }
}
