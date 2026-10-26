using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace StreamFin;

public sealed record Playback(string Url, string Secret, IReadOnlyList<string> Subtitles);

public sealed class StreamRelay(IServerApplicationHost host, SafeHttp http, IMediaEncoder encoder)
{
    // Jellyfin forwards User-Agent to both FFprobe and FFmpeg; arbitrary headers are not forwarded by FFprobe.
    // FFmpeg's HLS reader repeats it on every playlist and segment request.
    public const string Header = "User-Agent";
    private readonly string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();

    // Live is rechecked on every request and between chunks, so revoking a user, a source,
    // or a content filter stops playback that is already running.
    private sealed class Ticket(Guid userId, bool hls, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        public Guid UserId { get; } = userId;
        public bool Hls { get; } = hls;
        public Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> Resolve { get; } = resolve;
        public Func<bool> Live { get; } = live;
        public DateTime Expires { get; } = DateTime.UtcNow.AddHours(12);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ResolvedVideo? Video { get; set; }
        public HlsLinks Links { get; } = new();
        public required ResolvedVideo First { get; init; }
        // Jellyfin's own HTTP client fetches external subtitles and cannot send the secret header.
        public string SubtitleKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    // An HLS session starts from the resolution Jellyfin just probed; resolving again costs
    // several page loads for film sources and can land on a different server.
    public Playback Create(Guid userId, ResolvedVideo resolved, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        var hls = resolved.Hls;
        foreach (var pair in tickets.Where(p => p.Value.Expires < DateTime.UtcNow)) tickets.TryRemove(pair.Key, out _);
        if (tickets.Count >= 256) throw new ProviderException("Too many playback sessions. Please try again later.");
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var ticket = tickets[id] = new(userId, hls, resolve, live) { Video = hls ? resolved : null, First = resolved };
        var root = host.GetLocalApiUrl("127.0.0.1", "http", host.HttpPort).TrimEnd('/') + "/StreamFin/Internal/Stream/" + id;
        // The extension lets FFmpeg pick its HLS reader before reading a byte.
        return new(root + (hls ? "/master.m3u8" : ""), secret,
            (resolved.Subtitles ?? []).Select((_, i) => $"{root}/sub/{i}.vtt?key={ticket.SubtitleKey}").ToArray());
    }

    // The media source both channels hand to Jellyfin: probed through the relay, with any
    // subtitle tracks attached as external text streams that Jellyfin fetches server-side.
    // Jellyfin only extracts embedded subtitles from local files, so remote ones must be external.
    public async Task<MediaSourceInfo> MediaSourceAsync(Guid userId, ResolvedVideo resolved, string name,
        Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live, CancellationToken ct)
    {
        var playback = Create(userId, resolved, resolve, live);
        var media = new MediaSourceInfo
        {
            Path = playback.Url, Protocol = MediaProtocol.Http, Container = resolved.Hls ? "hls" : "mp4", IsRemote = true,
            Name = name, SupportsDirectPlay = false, SupportsDirectStream = false, SupportsTranscoding = true, SupportsProbing = true,
            RequiredHttpHeaders = new Dictionary<string, string> { [Header] = playback.Secret }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token);
        var streams = info.MediaStreams.Where(s => s.Type != MediaStreamType.Subtitle).ToList();
        var subtitles = resolved.Subtitles ?? [];
        for (var i = 0; i < subtitles.Count; i++)
            streams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle, Index = streams.Count, Codec = "vtt", Path = playback.Subtitles[i],
                IsExternal = true, SupportsExternalStream = true, Title = subtitles[i].Label, IsDefault = i == 0 && subtitles[i].Language == "en",
                Language = subtitles[i].Language == "und" ? null : subtitles[i].Language
            });
        media.MediaStreams = streams;
        media.RunTimeTicks = info.RunTimeTicks;
        media.Bitrate = info.Bitrate;
        media.Size = info.Size;
        media.Container = info.Container;
        return media;
    }

    private Ticket? Authorized(HttpContext context, string id)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString()), Encoding.UTF8.GetBytes(secret))
            && tickets.TryGetValue(id, out var ticket) && ticket.Expires > DateTime.UtcNow && Plugin.Allows(ticket.UserId) && ticket.Live()
            ? ticket : null;
    }

    // Resolve once per HLS session: the playlist tokens are tied to that resolution.
    private static async Task<ResolvedVideo> VideoAsync(Ticket ticket, CancellationToken ct)
    {
        if (ticket.Video is { } known) return known;
        await ticket.Gate.WaitAsync(ct);
        try { return ticket.Video ??= (await ticket.Resolve(ct))[0]; }
        finally { ticket.Gate.Release(); }
    }

    public async Task ServeFileAsync(HttpContext context, string id)
    {
        if (Authorized(context, id) is not { Hls: false } ticket) { context.Response.StatusCode = 403; return; }
        if (!ValidRange(context, out var range)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        // Re-resolve on each new read/seek. Jellyfin caches the stable loopback URL, not the expiring CDN URL.
        var video = (await ticket.Resolve(timeout.Token))[0];
        // Reviewed anime hosts keep their strict rules; film players live on rotating hosts.
        await ProxyAsync(context, ticket, video.Url, video.Headers, range, open: !SafeHttp.Allowed(video.Url, true), timeout.Token);
    }

    public async Task ServeMasterAsync(HttpContext context, string id)
    {
        if (Authorized(context, id) is not { Hls: true } ticket) { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var video = await VideoAsync(ticket, timeout.Token);
        var text = await http.TextAsync(video.Url, timeout.Token, headers: video.Headers, open: true);
        var master = Hls.IsMaster(text) ? Hls.RewriteMaster(text, video.Url, ticket.Links) : Hls.WrapMedia(ticket.Links.Add(video.Url, playlist: true));
        await WriteAsync(context, master, "application/vnd.apple.mpegurl");
    }

    public async Task ServePartAsync(HttpContext context, string id, int index)
    {
        if (Authorized(context, id) is not { Hls: true } ticket || ticket.Links.Get(index) is not { } upstream) { context.Response.StatusCode = 403; return; }
        if (!ValidRange(context, out var range)) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var video = await VideoAsync(ticket, timeout.Token);
        if (ticket.Links.IsPlaylist(index))
        {
            var text = await http.TextAsync(upstream, timeout.Token, headers: video.Headers, open: true);
            await WriteAsync(context, Hls.RewriteMedia(text, upstream, ticket.Links), "application/vnd.apple.mpegurl");
            return;
        }
        await ProxyAsync(context, ticket, upstream, video.Headers, range, open: true, timeout.Token);
    }

    public async Task ServeSubtitleAsync(HttpContext context, string id, int index, string key)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null || !IPAddress.IsLoopback(address) || !tickets.TryGetValue(id, out var ticket) || ticket.Expires <= DateTime.UtcNow
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(ticket.SubtitleKey))
            || !Plugin.Allows(ticket.UserId) || !ticket.Live() || ticket.First.Subtitles is not { } subtitles || index < 0 || index >= subtitles.Count)
        { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var text = await http.TextAsync(subtitles[index].Url, timeout.Token, headers: ticket.First.Headers, open: true);
        await WriteAsync(context, Hls.WebVtt(text), "text/vtt");
    }

    private static bool ValidRange(HttpContext context, out string range)
    {
        range = context.Request.Headers.Range.ToString();
        if (range.Length == 0 || (System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1)) return true;
        context.Response.StatusCode = 416;
        return false;
    }

    private static async Task WriteAsync(HttpContext context, string body, string type)
    {
        context.Response.ContentType = type;
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.WriteAsync(body, context.RequestAborted);
    }

    private async Task ProxyAsync(HttpContext context, Ticket ticket, Uri upstream, IReadOnlyDictionary<string, string> headers, string range, bool open, CancellationToken ct)
    {
        using var response = await http.OpenAsync(upstream, true, ct, range.Length == 0 ? null : range,
            head: HttpMethods.IsHead(context.Request.Method), headers: headers, open: open);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
        {
            if (response.Content.Headers.TryGetValues(name, out var contentValues)) context.Response.Headers[name] = contentValues.ToArray();
            else if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
        }
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[65536];
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, idle.Token);
                if (read == 0) break;
                if (!Plugin.Allows(ticket.UserId) || !ticket.Live()) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        // FFprobe and seeking FFmpeg hang up mid-segment all the time; that is not an error.
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException) { }
    }
}

// Upstream addresses never reach FFmpeg. Each one gets a small index in the ticket,
// and FFmpeg only ever sees loopback paths built from those indexes.
public sealed class HlsLinks
{
    public const int Limit = 20000;
    private readonly List<(Uri Url, bool Playlist)> links = [];
    private readonly Dictionary<Uri, int> known = [];
    public int Add(Uri url, bool playlist)
    {
        SafeHttp.ValidateOpen(url);
        lock (links)
        {
            if (known.TryGetValue(url, out var existing)) return existing;
            if (links.Count >= Limit) throw new ProviderException("The stream playlist is too large.");
            links.Add((url, playlist));
            return known[url] = links.Count - 1;
        }
    }
    public Uri? Get(int index) { lock (links) return index >= 0 && index < links.Count ? links[index].Url : null; }
    public bool IsPlaylist(int index) { lock (links) return links[index].Playlist; }
}

public static class Hls
{
    private static readonly Regex UriAttribute = new("URI=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(2));
    private static readonly Regex Bandwidth = new(@"[:,]BANDWIDTH=(\d+)", RegexOptions.None, TimeSpan.FromSeconds(1));
    private static readonly Regex Group = new("AUDIO=\"([^\"]*)\"", RegexOptions.None, TimeSpan.FromSeconds(1));

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);
    private static string[] Lines(string text)
    {
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal)) throw new ProviderException("The source did not return an HLS playlist.");
        return text.Replace("\r\n", "\n").Split('\n');
    }
    private static Uri Absolute(Uri playlist, string reference) =>
        Uri.TryCreate(playlist, reference.Trim(), out var url) ? url : throw new ProviderException("The stream playlist has an invalid address.");

    // Segment names keep a media extension: FFmpeg refuses HLS segments whose name it does not recognise.
    private static string Extension(Uri url)
    {
        var ext = Path.GetExtension(url.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return ext is "m4s" or "mp4" or "aac" or "ts" or "vtt" or "webvtt" or "m4a" or "m4v" or "cmfv" or "cmfa" ? ext : "ts";
    }
    private static string Part(int index, bool playlist, Uri url) => $"p/{index}.{(playlist ? "m3u8" : Extension(url))}";

    // Keep the best video variant and the renditions it uses. Jellyfin transcodes for each
    // client anyway, and every variant kept would be another stream FFprobe has to open.
    public static string RewriteMaster(string text, Uri playlist, HlsLinks links)
    {
        var lines = Lines(text);
        (long Bandwidth, int Line, int Uri)? best = null;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var next = Array.FindIndex(lines, i + 1, l => l.Trim().Length > 0 && !l.StartsWith('#'));
            if (next < 0) continue;
            var bandwidth = Bandwidth.Match(lines[i]) is { Success: true } m ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            if (best is null || bandwidth > best.Value.Bandwidth) best = (bandwidth, i, next);
        }
        if (best is not { } chosen) throw new ProviderException("The stream playlist lists no playable variant.");
        var audio = Group.Match(lines[chosen.Line]) is { Success: true } g ? g.Groups[1].Value : null;
        var output = new StringBuilder("#EXTM3U\n");
        foreach (var line in lines)
        {
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)) continue;
            // Subtitle renditions are dropped: Jellyfin cannot extract them from a remote stream.
            if (!(audio is not null && line.Contains("TYPE=AUDIO", StringComparison.Ordinal) && line.Contains($"GROUP-ID=\"{audio}\"", StringComparison.Ordinal))) continue;
            output.Append(UriAttribute.Replace(line, m => $"URI=\"{Part(links.Add(Absolute(playlist, m.Groups[1].Value), true), true, playlist)}\"")).Append('\n');
        }
        var info = Regex.Replace(lines[chosen.Line], ",(?:SUBTITLES|CLOSED-CAPTIONS)=\"[^\"]*\"", "", RegexOptions.None, TimeSpan.FromSeconds(1));
        var variant = Absolute(playlist, lines[chosen.Uri]);
        output.Append(info).Append('\n').Append(Part(links.Add(variant, true), true, variant)).Append('\n');
        return output.ToString();
    }

    public static string WrapMedia(int index) => $"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\np/{index}.m3u8\n";

    public static int FirstVariant(string master)
    {
        var line = master.Split('\n').First(l => l.StartsWith("p/", StringComparison.Ordinal));
        return int.Parse(line[2..line.IndexOf('.')], CultureInfo.InvariantCulture);
    }

    public static string RewriteMedia(string text, Uri playlist, HlsLinks links)
    {
        var output = new StringBuilder();
        foreach (var raw in Lines(text))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                // Keys, initialisation segments and nested renditions travel through the relay too.
                output.Append(line.StartsWith("#EXT-X-KEY", StringComparison.Ordinal) || line.StartsWith("#EXT-X-MAP", StringComparison.Ordinal)
                    || line.StartsWith("#EXT-X-SESSION-KEY", StringComparison.Ordinal)
                    ? UriAttribute.Replace(line, m => { var url = Absolute(playlist, m.Groups[1].Value); return $"URI=\"{Part(links.Add(url, false), false, url)}\""; })
                    : line).Append('\n');
                continue;
            }
            var segment = Absolute(playlist, line);
            // Inside p/, so a bare name resolves to the sibling relay path.
            output.Append(Part(links.Add(segment, false), false, segment)[2..]).Append('\n');
        }
        return output.ToString();
    }

    public static double Duration(string text) => Regex.Matches(text, @"#EXTINF:\s*([0-9.]+)", RegexOptions.None, TimeSpan.FromSeconds(2))
        .Sum(m => double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0);

    // Players hand out WebVTT or SubRip; Jellyfin gets WebVTT either way.
    public static string WebVtt(string text)
    {
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (text.TrimStart().StartsWith("WEBVTT", StringComparison.Ordinal)) return text;
        if (!Regex.IsMatch(text, @"\d{2}:\d{2}:\d{2}[,.]\d{3}\s*-->", RegexOptions.None, TimeSpan.FromSeconds(2))) throw new ProviderException("The subtitle file is not SubRip or WebVTT.");
        return "WEBVTT\n\n" + Regex.Replace(text, @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2", RegexOptions.None, TimeSpan.FromSeconds(2));
    }
}

[ApiController]
[Route("StreamFin/Internal/Stream")]
public sealed class StreamController(StreamRelay relay) : ControllerBase
{
    // FFmpeg cannot carry the user's Jellyfin session. These routes are loopback-only and additionally
    // require a random per-process header, an opaque ticket, and a still-authorized owner.
    [AllowAnonymous, HttpGet("{ticket}"), HttpHead("{ticket}")]
    public Task Stream(string ticket) => relay.ServeFileAsync(HttpContext, ticket);

    [AllowAnonymous, HttpGet("{ticket}/master.m3u8"), HttpHead("{ticket}/master.m3u8")]
    public Task Master(string ticket) => relay.ServeMasterAsync(HttpContext, ticket);

    [AllowAnonymous, HttpGet("{ticket}/p/{index:int}.{ext:length(2,6)}"), HttpHead("{ticket}/p/{index:int}.{ext:length(2,6)}")]
    public Task Part(string ticket, int index) => relay.ServePartAsync(HttpContext, ticket, index);

    // Fetched by Jellyfin's subtitle encoder, which sends its own User-Agent; the per-ticket key replaces the header.
    [AllowAnonymous, HttpGet("{ticket}/sub/{index:int}.vtt")]
    public Task Subtitle(string ticket, int index, [FromQuery] string key) => relay.ServeSubtitleAsync(HttpContext, ticket, index, key ?? "");
}
