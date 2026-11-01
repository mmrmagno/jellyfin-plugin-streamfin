using System.Diagnostics;
using System.Net;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Monochrome's API and its audio host fail together, so one health covers both: the catalog's lookups and the
// audio search skip it at once while it is not healthy.
public sealed class MonochromeHealth(ILogger<MonochromeHealth>? logger = null, bool probed = true)
{
    public CatalogBreaker Breaker { get; } = new(probed: probed) { Name = "Monochrome", Logger = logger };
    public bool Usable => Breaker.Usable(DateTime.UtcNow);

    // The last song that played from Monochrome: the prober asks its audio host for one byte of it.
    public Uri? LastTrack { get; set; }
}

// Checks Monochrome in the background: once at startup, then while it is down or recovering, at the pauses its health
// asks for. A check is one small catalog call plus one byte of a song that played recently, each held to what a healthy
// Monochrome manages.
public sealed class MonochromeProbe(MonochromeHealth health, MusicCatalog catalog, SafeHttp http, ILogger<MonochromeProbe> logger) : BackgroundService
{
    public static readonly TimeSpan ApiSlow = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan AudioSlow = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim running = new(1, 1);

    private static bool Wanted => Plugin.Instance?.Configuration is { Enabled: true, MusicEnabled: true, MusicMonochrome: true };

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var started = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var wait = !started ? TimeSpan.Zero : health.Breaker.Status(now).NextCheck is { } next ? next - now : TimeSpan.FromSeconds(10);
                if (wait > TimeSpan.Zero || !Wanted || await CheckNowAsync(stop) is null)
                {
                    await Task.Delay(wait > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 30)) : TimeSpan.FromSeconds(10), stop);
                    continue;
                }
                started = true;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("StreamFin music: the Monochrome check failed ({Reason})", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    // One check now, reported to Monochrome's health. Null when it was skipped: another check is running, or the
    // catalog budget is spent.
    public async Task<(string? Problem, TimeSpan Took)?> CheckNowAsync(CancellationToken ct)
    {
        if (!await running.WaitAsync(0, ct)) return null;
        try
        {
            if (!catalog.ProbeBudget()) return null;
            var clock = Stopwatch.StartNew();
            var track = health.LastTrack;
            var problem = await CheckAsync(catalog.ProbeAsync, track is null ? null : async token =>
            {
                using var response = await http.MediaAsync(track, AudioSlow + TimeSpan.FromSeconds(1), token, "bytes=0-0");
                return response.StatusCode;
            }, ct);
            if (problem == "gone") (health.LastTrack, problem) = (null, null);
            health.Breaker.Checked(DateTime.UtcNow, problem);
            return (problem, clock.Elapsed);
        }
        finally { running.Release(); }
    }

    // Null when both answered in time; otherwise what went wrong. "gone" means the song is no longer there, which says
    // nothing about the host.
    public static async Task<string?> CheckAsync(Func<CancellationToken, Task> api, Func<CancellationToken, Task<HttpStatusCode>>? audio, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try { await api(ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return ex is RateLimitedException ? null : CatalogBreaker.Cause(ex);
        }
        if (clock.Elapsed > ApiSlow) return $"answered after {clock.Elapsed.TotalSeconds:0} s";
        if (audio is null) return null;
        clock.Restart();
        HttpStatusCode status;
        try { status = await audio(ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return "audio " + CatalogBreaker.Cause(ex);
        }
        if ((int)status >= 500) return "audio HTTP " + (int)status;
        if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) return "gone";
        return clock.Elapsed > AudioSlow ? $"audio answered after {clock.Elapsed.TotalSeconds:0} s" : null;
    }
}

public sealed record HealthLine(string State, DateTime Since, DateTime? NextCheck, string? LastError)
{
    public static HealthLine From(SourceStatus status) => new(status.State.ToString(), status.Since, status.NextCheck, status.LastError);
}

public sealed record MusicHealthResponse(HealthLine Monochrome, HealthLine Deezer, HealthLine YouTube, string? YouTubeProblem, bool YouTubeProxy, string? YtDlpVersion);

public sealed record SourceTest(bool Ran, string? Problem, double Seconds);

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/MusicHealth")]
public sealed class MusicHealthController(MonochromeHealth health, MonochromeProbe probe, MusicCatalog catalog, MusicAudio audio) : ControllerBase
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastTest = new();

    [HttpGet]
    public ActionResult<MusicHealthResponse> Get()
    {
        var now = DateTime.UtcNow;
        return Ok(new MusicHealthResponse(HealthLine.From(health.Breaker.Status(now)), HealthLine.From(catalog.DeezerStatus(now)),
            HealthLine.From(audio.YouTubeStatus(now)), YtDlp.Problem, YtDlp.ProxySetting is not null, YtDlp.Version));
    }

    // Each source can be tested once every 10 s after the last test ended, whoever asks; one running counts as now.
    [HttpPost("Probe")]
    public async Task<ActionResult<SourceTest>> Probe([FromQuery] string source, CancellationToken ct)
    {
        if (source is not ("monochrome" or "youtube")) return BadRequest();
        var now = DateTime.UtcNow;
        var last = LastTest.GetOrAdd(source, DateTime.MinValue);
        if (now - last < TimeSpan.FromSeconds(10) || !LastTest.TryUpdate(source, DateTime.MaxValue, last)) return StatusCode(429);
        try
        {
            var clock = Stopwatch.StartNew();
            if (source == "youtube") return Ok(new SourceTest(true, await audio.TestYouTubeAsync(ct), clock.Elapsed.TotalSeconds));
            return await probe.CheckNowAsync(ct) is var (problem, took)
                ? Ok(new SourceTest(true, problem, took.TotalSeconds))
                : Ok(new SourceTest(false, "A check is already running, or the catalog is busy. Try again in a moment.", 0));
        }
        finally { LastTest[source] = DateTime.UtcNow; }
    }
}
