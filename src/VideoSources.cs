using System.Collections.Concurrent;
using System.Diagnostics;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// One test per source at a time, and another only 10 s after the last ended, whoever asks.
public static class TestLimit
{
    private static readonly ConcurrentDictionary<string, DateTime> Last = new();

    public static bool TryStart(string source)
    {
        var last = Last.GetOrAdd(source, DateTime.MinValue);
        return DateTime.UtcNow - last >= TimeSpan.FromSeconds(10) && Last.TryUpdate(source, DateTime.MaxValue, last);
    }

    public static void End(string source) => Last[source] = DateTime.UtcNow;
}

// How each video source is doing: down after a few real outages (no answer, no connection, a server error, never
// "this episode is not there"), then checked in the background with a growing pause until it answers again, while
// playback goes straight to another site. Also how long each takes to start an episode, so the fastest is tried first.
public sealed class VideoHealth(ILogger<VideoHealth>? logger = null)
{
    public static readonly string[] Sources = [.. SourceRegistry.Order, "flix"];
    private static readonly TimeSpan Slow = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, CatalogBreaker> breakers = new();
    private readonly ConcurrentDictionary<string, double> speeds = new();

    public CatalogBreaker Breaker(string source) => breakers.GetOrAdd(source, id => new CatalogBreaker(limit: 3, window: TimeSpan.FromMinutes(10), slow: Slow, probed: true)
        { Name = Label(id), Logger = logger });

    public static string Label(string source) => source switch { "zoko" => "HiAnime", "animegg" => "AnimeGG", "animeheaven" => "AnimeHeaven", "flix" => "FlixHQ", _ => source };

    public bool Usable(string source) => Breaker(source).Usable(DateTime.UtcNow);

    // An episode that started: its time counts toward the site's usual start time (recent starts count most), and
    // proves the site works, counting as a passed check while it was down. Slow is not down: the time never counts
    // against it.
    public void Started(string source, TimeSpan took)
    {
        speeds.AddOrUpdate(source, took.TotalSeconds, (_, old) => old * 0.7 + took.TotalSeconds * 0.3);
        var breaker = Breaker(source);
        if (breaker.Usable(DateTime.UtcNow)) breaker.Success(DateTime.UtcNow);
        else breaker.Checked(DateTime.UtcNow, null);
    }

    // A check's result. Only an outage counts against the site; a check that ran but found nothing (the test title
    // gone from the site) says the site answers. A manual test of a working site counts as one failure, not as down.
    public void Checked(string source, VideoCheck check, bool manual)
    {
        var breaker = Breaker(source);
        if (!check.Outage) breaker.Checked(DateTime.UtcNow, null);
        else if (manual && breaker.Usable(DateTime.UtcNow)) breaker.Failure(DateTime.UtcNow, check.Problem ?? "no answer");
        else breaker.Checked(DateTime.UtcNow, check.Problem);
    }

    // Changes whenever a site goes down or comes back, so cached listings that showed it otherwise are made again.
    public string Key => string.Concat(Sources.Select(source => Usable(source) ? "1" : "0"));

    // A failure counts only when it says the site itself is out.
    public void Failed(string source, Exception ex, CancellationToken caller)
    {
        if (CatalogBreaker.IsOutage(ex, caller)) Breaker(source).Failure(DateTime.UtcNow, CatalogBreaker.Cause(ex));
    }

    public double? Typical(string source) => speeds.TryGetValue(source, out var seconds) ? seconds : null;

    // Working sites first, the quickest first among them; sites not timed yet go after the timed ones, in the usual order.
    public IEnumerable<T> Fastest<T>(IEnumerable<T> items, Func<T, string> source) => items
        .Select((item, index) => (item, index)).OrderBy(p => Usable(source(p.item)) ? 0 : 1).ThenBy(p => Typical(source(p.item)) ?? double.MaxValue)
        .ThenBy(p => p.index).Select(p => p.item);

    // For a folder or a message: "usually starts in about 3 s", or since when it is down.
    public string? Describe(string source)
    {
        var status = Breaker(source).Status(DateTime.UtcNow);
        if (status.State != SourceHealth.Healthy)
            return $"{Label(source)} is not answering right now ({status.LastError}); StreamFin checks it again in the background"
                + (source == "flix" ? "." : " and plays from another site meanwhile.");
        return Typical(source) is { } seconds ? $"Usually starts in about {Math.Max(1, Math.Round(seconds)):0} s." : null;
    }
}

// What a check found: null when it works. An outage is no answer, no connection or a server error; anything else
// (nothing found, a page that changed) means the site answers.
public sealed record VideoCheck(string? Problem, bool Outage)
{
    public static readonly VideoCheck Works = new(null, false);
}

// The whole way to a playable video, for a title every site carries, so a check that passes means playing works.
public sealed class VideoChecks(IEnumerable<IAnimeSource> anime, TmdbCatalog tmdb, FlixSource flix)
{
    private static readonly Anime Frieren = new(154587, "Sousou no Frieren", ["Frieren: Beyond Journey's End", "Frieren"], 2023, "", null, 24, false, [], MalId: 52991, AvailableEpisodes: 28);
    private static readonly Film Inception = new(27205, FilmKind.Movie, "Inception", 2010, "", null, 148, false);

    public async Task<VideoCheck> CheckAsync(string source, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            switch (source)
            {
                case "tmdb":
                    if (!TmdbCatalog.HasKey) return new("no TMDB API key is set", false);
                    await tmdb.ProbeAsync(limit.Token);
                    return VideoCheck.Works;
                case "flix":
                    flix.Forget(Inception);
                    return (await flix.ResolveAsync(Inception, null, null, limit.Token)).Count > 0 ? VideoCheck.Works : new("no video was found", false);
                default:
                    if (anime.FirstOrDefault(a => a.Id == source) is not { } site) return new("unknown source", false);
                    var match = await SourceRegistry.MatchAsync(site, Frieren, limit.Token);
                    if (match.Titles.Count == 0) return new("search: " + (match.Error ?? "a well-known title was not found"), match.Error is not null);
                    var episodes = await site.EpisodesAsync(match.Titles[0].Id, Frieren, limit.Token);
                    if (episodes.Count == 0) return new("the episode list is empty", false);
                    return (await site.ResolveAsync(episodes[0].Id, limit.Token)).Count > 0 ? VideoCheck.Works : new("no video was found", false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new(ex is ProviderException ? ex.Message : CatalogBreaker.Cause(ex), CatalogBreaker.IsOutage(ex, ct));
        }
    }
}

// Checks a video source that is down, at the pauses its health asks for, until it answers three times in a row.
public sealed class VideoProbe(VideoHealth health, VideoChecks checks, ILogger<VideoProbe> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stop);
                if (Plugin.Instance?.Configuration is not { Enabled: true } config) continue;
                foreach (var source in VideoHealth.Sources)
                {
                    var status = health.Breaker(source).Status(DateTime.UtcNow);
                    if (status.State == SourceHealth.Healthy || status.NextCheck > DateTime.UtcNow) continue;
                    if (source == "flix" ? !FilmChannel.IsAvailable : !SourceRegistry.IsEnabled(source, config)) continue;
                    // Each site on its own: one that breaks in a new way must not stop the others being checked.
                    VideoCheck check;
                    try { check = await checks.CheckAsync(source, stop); }
                    catch (Exception ex) when (!stop.IsCancellationRequested) { check = new(ex.GetType().Name, true); }
                    health.Checked(source, check, manual: false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("StreamFin video source check failed ({ErrorType})", ex.GetType().Name); }
        }
    }
}

public sealed record VideoSourceLine(string Name, HealthLine Health, double? TypicalSeconds);

// The settings page: each video source's state, and "Test now", which also counts as a check of its health.
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/VideoSources")]
public sealed class VideoSourcesController(VideoHealth health, VideoChecks checks) : ControllerBase
{
    public static readonly string[] Sources = [.. VideoHealth.Sources, "tmdb"];

    [HttpGet]
    public ActionResult<Dictionary<string, VideoSourceLine>> Get() => Ok(VideoHealth.Sources.ToDictionary(source => source,
        source => new VideoSourceLine(VideoHealth.Label(source), HealthLine.From(health.Breaker(source).Status(DateTime.UtcNow)), health.Typical(source))));

    [HttpPost("Test")]
    public async Task<ActionResult<SourceTest>> Test([FromQuery] string source, CancellationToken ct)
    {
        if (!Sources.Contains(source)) return BadRequest();
        if (!TestLimit.TryStart("video/" + source)) return StatusCode(429);
        var clock = Stopwatch.StartNew();
        try
        {
            VideoCheck check;
            try { check = await checks.CheckAsync(source, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { check = new(ex.GetType().Name, true); }
            if (source != "tmdb") health.Checked(source, check, manual: true);
            return Ok(new SourceTest(true, check.Problem, clock.Elapsed.TotalSeconds));
        }
        finally { TestLimit.End("video/" + source); }
    }
}
