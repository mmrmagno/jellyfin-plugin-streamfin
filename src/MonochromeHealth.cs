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

// Checks Monochrome in the background while it is down or recovering, at the pauses its health asks for. A check is
// one small catalog call plus one byte of a song that played recently, each held to what a healthy Monochrome manages.
public sealed class MonochromeProbe(MonochromeHealth health, MusicCatalog catalog, SafeHttp http, ILogger<MonochromeProbe> logger) : BackgroundService
{
    public static readonly TimeSpan ApiSlow = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan AudioSlow = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var status = health.Breaker.Status(now);
                var wait = status.NextCheck is { } next ? next - now : TimeSpan.FromSeconds(10);
                if (wait > TimeSpan.Zero || Plugin.Instance?.Configuration is not { Enabled: true, MusicEnabled: true } || !catalog.ProbeBudget())
                {
                    await Task.Delay(wait > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 30)) : TimeSpan.FromSeconds(10), stop);
                    continue;
                }
                var track = health.LastTrack;
                var problem = await CheckAsync(catalog.ProbeAsync, track is null ? null : async ct =>
                {
                    using var response = await http.MediaAsync(track, AudioSlow + TimeSpan.FromSeconds(1), ct, "bytes=0-0");
                    return response.StatusCode;
                }, stop);
                if (problem == "gone") (health.LastTrack, problem) = (null, null);
                health.Breaker.Checked(DateTime.UtcNow, problem);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("StreamFin music: the Monochrome check failed ({Reason})", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(30), stop).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
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

public sealed record MusicHealthResponse(HealthLine Monochrome, HealthLine Deezer, HealthLine YouTube, string? YouTubeProblem);

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/MusicHealth")]
public sealed class MusicHealthController(MonochromeHealth health, MusicCatalog catalog, MusicAudio audio) : ControllerBase
{
    [HttpGet]
    public ActionResult<MusicHealthResponse> Get()
    {
        var now = DateTime.UtcNow;
        return Ok(new MusicHealthResponse(HealthLine.From(health.Breaker.Status(now)), HealthLine.From(catalog.DeezerStatus(now)),
            HealthLine.From(audio.YouTubeStatus(now)), YtDlp.Problem));
    }
}
