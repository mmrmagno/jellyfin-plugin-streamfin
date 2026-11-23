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
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record Playback(string Id, string Url, string Secret, IReadOnlyList<string> Subtitles);

public sealed class StreamRelay(IServerApplicationHost host, SafeHttp http, IMediaEncoder encoder, MusicCache songs)
{
    // Jellyfin forwards User-Agent to both FFprobe and FFmpeg; arbitrary headers are not forwarded by FFprobe.
    // FFmpeg's HLS reader repeats it on every playlist and segment request.
    public const string Header = "User-Agent";
    private readonly byte[] secret = RandomNumberGenerator.GetBytes(32);
    // Each ticket has its own header value: Jellyfin shows a playback's required headers to the user who plays it, and
    // one that leaks must open nothing but that user's own stream.
    private string TicketSecret(string id) => Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(id)));
    private readonly ConcurrentDictionary<string, Ticket> tickets = new();
    // A song's file from one source never changes, and the catalog throttles bursts, so each one is probed once.
    // Kept as JSON so every playback gets its own copy of the streams to adjust.
    private readonly MemoryCache audioProbes = new(new MemoryCacheOptions { SizeLimit = 5000 });
    private sealed record AudioProbe(string Streams, long? RunTimeTicks, int? Bitrate, long? Size, string? Container);

    // Live is rechecked on every request and between chunks, so revoking a user, a source,
    // or a content filter stops playback that is already running.
    private sealed class Ticket(Guid userId, bool hls, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        public Guid UserId { get; } = userId;
        public bool Hls { get; } = hls;
        public Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> Resolve { get; } = resolve;
        public Func<bool> Live { get; } = live;
        public DateTime Created { get; } = DateTime.UtcNow;
        public DateTime Expires { get; } = DateTime.UtcNow.AddHours(12);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        // Set once anything reads the stream, so versions that were only listed give way first.
        public bool Used { get; set; }
        // Segments that could not be fetched in a row. A host that has stopped answering is not asked again and again by
        // FFprobe walking the whole playlist: after a few failures the next segments fail at once, for 20 s, then one
        // request is let through again in case the host is back.
        public int SegmentFailures;
        public long LastFailure;
        // Movies & TV tickets follow the quality settings when choosing a variant; anime and music keep the best one.
        public bool FilmQuality { get; init; }
        // Segments whose leading image was cut off are always sent whole, so a later byte range cannot land off by the image.
        public ConcurrentDictionary<int, int> Stripped { get; } = new();
        public ResolvedVideo? Video { get; set; }
        public HlsLinks Links { get; } = new();
        public required ResolvedVideo First { get; init; }
        // Jellyfin's own HTTP client fetches external subtitles and cannot send the secret header.
        public string SubtitleKey { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    }

    // A session starts from the resolution Jellyfin just probed; resolving again costs several
    // page loads for film sources and can land on a different server or file.
    public Playback Create(Guid userId, ResolvedVideo resolved, Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live, bool filmQuality = false)
    {
        var hls = resolved.Hls;
        foreach (var pair in tickets.Where(p => p.Value.Expires < DateTime.UtcNow)) tickets.TryRemove(pair.Key, out _);
        // Failed and abandoned attempts pile up, so make room by dropping the oldest instead of refusing.
        // A title lists several versions, each with its own ticket. Tickets from the last two minutes (the versions being
        // listed right now) stay; among older ones, those never played go first, then the oldest.
        var recent = DateTime.UtcNow.AddMinutes(-2);
        foreach (var (limit, scope) in new[] { (128, tickets.Where(p => p.Value.UserId == userId)), (1024, tickets) })
        {
            var all = scope.ToList();
            foreach (var pair in all.OrderBy(p => p.Value.Created > recent).ThenBy(p => p.Value.Used).ThenBy(p => p.Value.Created).Take(all.Count - (limit - 1)).ToList())
                tickets.TryRemove(pair.Key, out _);
        }
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var ticket = tickets[id] = new(userId, hls, resolve, live) { Video = resolved, First = resolved, FilmQuality = filmQuality };
        var root = host.GetLocalApiUrl("127.0.0.1", "http", host.HttpPort).TrimEnd('/') + "/StreamFin/Internal/Stream/" + id;
        // The extension lets FFmpeg pick its HLS reader before reading a byte.
        return new(id, root + (hls ? "/master.m3u8" : ""), TicketSecret(id),
            (resolved.Subtitles ?? []).Select((_, i) => $"{root}/sub/{i}.vtt?key={ticket.SubtitleKey}").ToArray());
    }

    // Whether a playback address handed out earlier still works: tickets expire, and the oldest give way to new ones.
    public bool Holds(string? url) => url?.Split("/StreamFin/Internal/Stream/") is [_, var rest] && tickets.TryGetValue(rest.Split('/')[0], out var ticket)
        && ticket.Expires > DateTime.UtcNow;

    // Jellyfin only extracts embedded subtitles from local files, so remote ones must be external.
    public async Task<MediaSourceInfo> MediaSourceAsync(Guid userId, ResolvedVideo resolved, string name,
        Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live, CancellationToken ct)
    {
        var playback = Create(userId, resolved, resolve, live);
        try { return await ProbeAsync(playback, resolved, name, ct); }
        catch { tickets.TryRemove(playback.Id, out _); throw; }
    }

    // A version that is listed but not yet probed: it plays like any other once picked, and is probed then.
    // Until it is probed, a listed version describes its picture from its playlist (height, codec) and assumes stereo
    // AAC, so a player can start it straight away; the probe replaces the guess in the same object, which Jellyfin
    // keeps in its own list of versions for a few minutes.
    public sealed class Prepared(Playback playback, ResolvedVideo video, MediaSourceInfo media)
    {
        public Playback Playback { get; } = playback;
        public ResolvedVideo Video { get; } = video;
        public MediaSourceInfo Media { get; } = media;
        public bool Probed { get; set; }
        public bool Failed { get; set; }
        // One probe of a version at a time: a pick and the background check never probe it twice over.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
    public Prepared Prepare(Guid userId, ResolvedVideo resolved, string name, string id, int height, bool? h264,
        Func<CancellationToken, Task<IReadOnlyList<ResolvedVideo>>> resolve, Func<bool> live)
    {
        var playback = Create(userId, resolved, resolve, live, filmQuality: true);
        var media = Base(playback, resolved, name);
        media.Id = id;
        // No audio is guessed: an app sends the audio track chosen on the page by number, and a guessed number would pick
        // the wrong one once the probe finds the real order. Subtitles are numbered from 100 both before and after the probe.
        var guessed = new List<MediaStream>
        {
            new() { Type = MediaStreamType.Video, Index = 0, Codec = h264 == false ? "hevc" : "h264", Height = height > 0 ? height : null,
                Width = height > 0 ? height * 16 / 9 : null, IsDefault = true }
        };
        guessed.AddRange(Subtitles(playback, resolved, ListedSubtitles));
        media.MediaStreams = guessed;
        return new(playback, resolved, media);
    }

    // Where a listed version's subtitle tracks are numbered, well past any stream a probe finds.
    public const int ListedSubtitles = 100;

    public async Task<MediaSourceInfo> ProbeAsync(Prepared prepared, CancellationToken ct)
    {
        await prepared.Gate.WaitAsync(ct);
        try
        {
            if (prepared.Probed) return prepared.Media;
            var probed = await ProbeAsync(prepared.Playback, prepared.Video, prepared.Media.Name, ct, ListedSubtitles);
            var media = prepared.Media;
            media.MediaStreams = probed.MediaStreams;
            media.RunTimeTicks = probed.RunTimeTicks;
            media.Bitrate = probed.Bitrate;
            media.Size = probed.Size;
            media.Container = probed.Container;
            prepared.Probed = true;
            prepared.Failed = false;
            return media;
        }
        // The ticket stays: a version that failed once may open when it is picked later, and it is still listed.
        catch { prepared.Failed = true; throw; }
        finally { prepared.Gate.Release(); }
    }

    private static MediaSourceInfo Base(Playback playback, ResolvedVideo resolved, string name) => new()
    {
        // Audio is direct played so the FLAC reaches the client untouched: with a required header, clients
        // ask Jellyfin's static stream, which fetches the loopback URL itself and forwards User-Agent and Range.
        // Jellyfin 12 disables direct stream for every HTTP source, and a transcode follows the client's
        // profile, which is often lossy.
        Path = playback.Url, Protocol = MediaProtocol.Http, Container = resolved.Ticket?.Container ?? (resolved.Hls ? "hls" : "mp4"), IsRemote = true,
        Name = name, SupportsDirectPlay = resolved.Audio, SupportsDirectStream = resolved.Audio, SupportsTranscoding = true, SupportsProbing = true,
        RequiredHttpHeaders = new Dictionary<string, string> { [Header] = playback.Secret }
    };

    // Codec "webvtt", not "vtt": when the codec equals the client's format, Jellyfin hands the client this Path, a
    // loopback relay address it cannot reach. Any other name makes Jellyfin serve the track from its own endpoint.
    private static List<MediaStream> Subtitles(Playback playback, ResolvedVideo resolved, int first)
    {
        var subtitles = resolved.Subtitles ?? [];
        return subtitles.Select((subtitle, i) => new MediaStream
        {
            Type = MediaStreamType.Subtitle, Index = first + i, Codec = "webvtt", Path = playback.Subtitles[i],
            IsExternal = true, SupportsExternalStream = true, Title = subtitle.Label, IsDefault = i == 0 && subtitle.Language == "en",
            Language = subtitle.Language == "und" ? null : subtitle.Language
        }).ToList();
    }

    private async Task<MediaSourceInfo> ProbeAsync(Playback playback, ResolvedVideo resolved, string name, CancellationToken ct, int? subtitlesFrom = null)
    {
        var media = Base(playback, resolved, name);
        // YouTube's addresses change on every lookup, so probes are kept per song and format instead.
        if (resolved.Ticket is { } song) return await ProbeAudioAsync(media, song.TrackId + ":" + song.Container, song.Container, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token);
        var streams = info.MediaStreams.Where(s => s.Type != MediaStreamType.Subtitle).ToList();
        streams.AddRange(Subtitles(playback, resolved, subtitlesFrom ?? streams.Count));
        media.MediaStreams = streams;
        media.RunTimeTicks = info.RunTimeTicks;
        media.Bitrate = info.Bitrate;
        media.Size = info.Size;
        media.Container = info.Container;
        return media;
    }

    private async Task<MediaSourceInfo> ProbeAudioAsync(MediaSourceInfo media, string key, string container, CancellationToken ct)
    {
        if (!audioProbes.TryGetValue<AudioProbe>(key, out var probe))
        {
            try
            {
                // Well inside the minute a player or proxy waits: a song that needs longer to describe plays undescribed.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Audio, ExtractChapters = false }, timeout.Token);
                var audio = info.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).ToList();
                if (audio.Count == 0) throw new ProviderException("The probe found no audio.");
                probe = new AudioProbe(System.Text.Json.JsonSerializer.Serialize(audio), info.RunTimeTicks, info.Bitrate, info.Size, info.Container);
                audioProbes.Set(key, probe, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromDays(1) });
            }
            // Each source sends one known format, so a probe it throttled must not stop playback; the player reads the details itself.
            // The plain description is kept a few minutes, so replaying a song whose probe failed does not wait again.
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var plain = new MediaStream { Type = MediaStreamType.Audio, Index = 0, Codec = container == "flac" ? "flac" : "aac", Channels = 2, ChannelLayout = "stereo", IsDefault = true };
                probe = new AudioProbe(System.Text.Json.JsonSerializer.Serialize(new List<MediaStream> { plain }), null, null, null, container);
                audioProbes.Set(key, probe, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
            }
        }
        media.MediaStreams = System.Text.Json.JsonSerializer.Deserialize<List<MediaStream>>(probe!.Streams) ?? [];
        media.RunTimeTicks = probe.RunTimeTicks;
        media.Bitrate = probe.Bitrate;
        media.Size = probe.Size;
        media.Container = probe.Container ?? container;
        return media;
    }

    private Ticket? Authorized(HttpContext context, string id)
    {
        var address = context.Connection.RemoteIpAddress;
        return address is not null && IPAddress.IsLoopback(address)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers[Header].ToString()), Encoding.UTF8.GetBytes(TicketSecret(id)))
            && tickets.TryGetValue(id, out var ticket) && ticket.Expires > DateTime.UtcNow && Plugin.Allows(ticket.UserId) && ticket.Live()
            ? Use(ticket) : null;
        static Ticket Use(Ticket ticket) { ticket.Used = true; return ticket; }
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
        // Keep the probed file. Jellyfin holds the stable loopback URL, so when the CDN link expires
        // (403/404/410) the episode is resolved again, once, and only another MP4 is accepted.
        var video = ticket.Video!;
        if (video.Ticket is { } song)
        {
            songs.Warm(song);
            context.Response.Headers.CacheControl = "no-store";
            if (await songs.ServeAsync(context, song, range, () => Plugin.Allows(ticket.UserId) && ticket.Live())) return;
            // A song kept on disk with no address to fall back to was evicted, so look again.
            if (song.Local) MusicAudio.Forget(song.TrackId);
        }
        // Reviewed anime hosts keep their strict rules; film players live on rotating hosts.
        if (!(video.Ticket is { Local: true })
            && await ProxyAsync(context, ticket, video.Url, video.Headers, range, open: !SafeHttp.Allowed(video.Url, true), timeout.Token, retryable: true, audio: video.Ticket?.ContentType)) return;
        await ticket.Gate.WaitAsync(timeout.Token);
        try
        {
            if (ReferenceEquals(ticket.Video, video))
            {
                if (video.Ticket is { } expired) MusicAudio.Forget(expired.TrackId);
                var fresh = (await ticket.Resolve(timeout.Token))[0];
                // A song never changes format partway through: the player has already read part of the other one.
                if (fresh.Hls || fresh.Ticket?.Container != video.Ticket?.Container) { context.Response.StatusCode = 502; return; }
                ticket.Video = fresh;
            }
            video = ticket.Video!;
        }
        finally { ticket.Gate.Release(); }
        if (video.Ticket is { } local && songs.Kept(local.TrackId) == local.Container && await songs.ServeAsync(context, local, range, () => Plugin.Allows(ticket.UserId) && ticket.Live())) return;
        await ProxyAsync(context, ticket, video.Url, video.Headers, range, open: !SafeHttp.Allowed(video.Url, true), timeout.Token, audio: video.Ticket?.ContentType);
    }

    public async Task ServeMasterAsync(HttpContext context, string id)
    {
        if (Authorized(context, id) is not { Hls: true } ticket) { context.Response.StatusCode = 403; return; }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var video = await VideoAsync(ticket, timeout.Token);
        var text = await http.TextAsync(video.Url, timeout.Token, headers: video.Headers, open: true);
        var master = Hls.IsMaster(text) ? (ticket.FilmQuality ? Hls.RewriteMaster(text, video.Url, ticket.Links, FilmSources.MaxHeight, FilmSources.PreferH264) : Hls.RewriteMaster(text, video.Url, ticket.Links)) : Hls.WrapMedia(ticket.Links.Add(video.Url, playlist: true, slot: "media"));
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
            await WriteAsync(context, Hls.RewriteMedia(text, upstream, ticket.Links, index), "application/vnd.apple.mpegurl");
            return;
        }
        if (Volatile.Read(ref ticket.SegmentFailures) >= 6 && DateTime.UtcNow.Ticks - Interlocked.Read(ref ticket.LastFailure) < TimeSpan.FromSeconds(20).Ticks)
        { context.Response.StatusCode = 502; return; }
        if (ticket.Stripped.ContainsKey(index)) range = "";
        try
        {
            await ProxyAsync(context, ticket, upstream, video.Headers, range, open: true, timeout.Token, segment: true, link: index);
            if (context.Response.StatusCode < 400) Interlocked.Exchange(ref ticket.SegmentFailures, 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            Interlocked.Increment(ref ticket.SegmentFailures);
            Interlocked.Exchange(ref ticket.LastFailure, DateTime.UtcNow.Ticks);
            if (!context.Response.HasStarted) context.Response.StatusCode = 502;
        }
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

    // Returns false, without writing anything, when a retryable request finds the link expired.
    private async Task<bool> ProxyAsync(HttpContext context, Ticket ticket, Uri upstream, IReadOnlyDictionary<string, string> headers, string range, bool open, CancellationToken ct, bool retryable = false, bool segment = false, string? audio = null, int link = -1)
    {
        using var response = await SafeHttp.PatientAsync(() => http.OpenAsync(upstream, true, ct, range.Length == 0 ? null : range,
            head: HttpMethods.IsHead(context.Request.Method), headers: headers, open: open), ct, attempts: audio is null ? 1 : 3);
        if (retryable && (int)response.StatusCode is 403 or 404 or 410) return false;
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.Headers.CacheControl = "no-store";
        foreach (var name in new[] { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges" })
        {
            if (response.Content.Headers.TryGetValues(name, out var contentValues)) context.Response.Headers[name] = contentValues.ToArray();
            else if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = values.ToArray();
        }
        // Jellyfin's static stream passes this on to the client; the catalog labels FLAC as octet-stream.
        if (audio is not null && response.IsSuccessStatusCode) context.Response.ContentType = audio;
        if (HttpMethods.IsHead(context.Request.Method)) return true;
        await using var input = await response.Content.ReadAsStreamAsync(context.RequestAborted);
        var buffer = new byte[65536];
        var first = true;
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, idle.Token);
                if (read == 0) break;
                // A "segment" that is really a playlist would make FFmpeg fetch its addresses directly.
                if (segment && first && Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 64)).TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('#'))
                {
                    if (!context.Response.HasStarted) context.Response.StatusCode = 502;
                    break;
                }
                var skip = 0;
                // FFmpeg asks for every segment from byte 0 ("bytes=0-"); that whole-file answer is shortened by the image.
                if (segment && first && (range.Length == 0 || range.Replace(" ", "") == "bytes=0-") && !context.Response.HasStarted
                    && Hls.ImagePrefix(buffer.AsSpan(0, read)) is > 0 and var image)
                {
                    skip = image;
                    if (link >= 0) ticket.Stripped[link] = image;
                    var total = response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength;
                    context.Response.StatusCode = 200;
                    context.Response.Headers.Remove("Content-Range");
                    context.Response.Headers.Remove("Accept-Ranges");
                    context.Response.ContentLength = total is long length ? length - skip : null;
                    context.Response.ContentType = "video/mp2t";
                }
                first = false;
                if (!Plugin.Allows(ticket.UserId) || !ticket.Live()) break;
                await context.Response.Body.WriteAsync(buffer.AsMemory(skip, read - skip), context.RequestAborted);
            }
        }
        // FFprobe and seeking FFmpeg hang up mid-segment all the time; that is not an error.
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException) { }
        return true;
    }
}
