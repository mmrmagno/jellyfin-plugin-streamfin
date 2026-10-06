using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AniFin;

public sealed class StreamRelay(IServerApplicationHost host, IAnimeSource source, SafeHttp http)
{
    // Jellyfin forwards User-Agent to both FFprobe and FFmpeg; arbitrary headers are not forwarded by FFprobe.
    public const string Header = "User-Agent";
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private sealed record Ticket(Guid UserId, string EpisodeId, DateTime Expires);
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();
    public (string Url, string Secret) Create(Guid userId, string episode, ResolvedVideo video)
    {
        foreach (var pair in tickets.Where(p => p.Value.Expires < DateTime.UtcNow)) tickets.TryRemove(pair.Key, out _);
        if (tickets.Count >= 256) throw new ProviderException("Too many playback sessions. Please try again later.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        tickets[id] = new(userId, episode, DateTime.UtcNow.AddHours(12));
        return (host.GetLocalApiUrl("127.0.0.1", "http", host.HttpPort).TrimEnd('/') + "/AniFin/Internal/Stream/" + id, secret);
    }

    public bool CanAccess(HttpContext context, string id)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address)
            && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString()), System.Text.Encoding.UTF8.GetBytes(secret))
            && tickets.TryGetValue(id, out var ticket) && ticket.Expires > DateTime.UtcNow && Plugin.Allows(ticket.UserId)
            && Plugin.Instance?.Configuration.AnimeGgEnabled == true;
    }

    public async Task ServeAsync(HttpContext context, string id)
    {
        if (!CanAccess(context, id)) { context.Response.StatusCode = 403; return; }
        var ticket = tickets[id];
        var range = context.Request.Headers.Range.ToString();
        if (range.Length > 0 && (!System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) || parsed.Unit != "bytes" || parsed.Ranges.Count != 1))
        { context.Response.StatusCode = 416; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Re-resolve on each new read/seek. Jellyfin caches the stable loopback URL, not the expiring CDN URL.
        var videos = await source.ResolveAsync(ticket.EpisodeId, timeout.Token);
        using var response = await http.OpenAsync(videos[0].Url, true, timeout.Token,
            range.Length == 0 ? null : range, head: HttpMethods.IsHead(context.Request.Method));
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
        {
            if (response.Content.Headers.TryGetValues(name, out var contentValues)) context.Response.Headers[name] = contentValues.ToArray();
            else if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
        }
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[65536];
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, idle.Token);
                if (read == 0) break;
                if (!Plugin.Allows(ticket.UserId)) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
    }
}

[ApiController]
[Route("AniFin/Internal/Stream")]
public sealed class StreamController(StreamRelay relay) : ControllerBase
{
    // FFmpeg cannot carry the user's Jellyfin session. This is loopback-only and additionally
    // requires a random per-process header, an opaque ticket, and a still-authorized owner.
    [AllowAnonymous]
    [HttpGet("{ticket}")]
    [HttpHead("{ticket}")]
    public Task Stream(string ticket) => relay.ServeAsync(HttpContext, ticket);
}
