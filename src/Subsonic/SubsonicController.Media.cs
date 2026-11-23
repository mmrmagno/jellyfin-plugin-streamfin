using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public sealed partial class SubsonicController
{
    // ---- Media

    private async Task<SubsonicNode?> CoverAsync()
    {
        var id = One("id") ?? throw Missing("id");
        string? address = null;
        MusicRelease? release = null;
        try
        {
            // Lists ask for a cover per album: what this user's library already knows costs the catalog nothing.
            if (ParseId(id, "al") is { } releaseId)
            {
                release = !catalog.HasRelease(releaseId) && (await AlbumsAsync(await store.ReadAsync(user.Id, Ct))).FirstOrDefault(a => a.Release.Id == releaseId)
                    is { Release.Artwork: not null } mine ? mine.Release : await catalog.ReleaseAsync(releaseId, Ct);
                address = release.Artwork;
            }
            else if (ParseTrack(id) is not null)
            {
                var (track, from) = await TrackAsync(id);
                (address, release) = (track.Artwork, from.Tracks.Count > 0 ? from : null);
            }
            else if (ParseId(id, "ar") is { } artistId) address = (await catalog.ArtistAsync(artistId, Ct)).Image;
            else if (ParsePlaylist(id) is { } playlistId)
            {
                var (library, _) = await LibraryAsync();
                address = library.Playlists.FirstOrDefault(p => p.Id == playlistId)?.Entries.FirstOrDefault()?.Track.Artwork;
            }
        }
        catch (ProviderException) { throw NotFound("Cover art"); }
        // A saved album's own cover first, then the kept copy of the catalog's. The parsers only keep artwork on the
        // catalog host; SafeHttp checks it again.
        string? path = null, type = null;
        if (release is not null && MusicSaver.SavedCover(release) is { } saved && MusicArtCache.TypeOf(saved) is { } savedType) (path, type) = (saved, savedType);
        else if (address is not null && await covers.GetAsync(address, catalog.DownForArt(address), Ct) is var (kept, keptType)) (path, type) = (kept, keptType);
        if (path is null || type is null) throw NotFound("Cover art");
        if (Int("size", 0, 0, 10_000) is > 0 and var size && await covers.ResizedAsync(path, size, images) is { } smaller) (path, type) = (smaller, "image/jpeg");
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotFound("Cover art"); }
        await using var _ = file;
        Response.ContentType = type;
        Response.Headers.CacheControl = "private, max-age=604800";
        // Jellyfin only adds CORS headers when a request has an Origin. Web players load a cover with <img> (no
        // Origin) and then fetch() it again to pick colours; without Vary the cached copy fails that check.
        Response.Headers.Vary = "Origin";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.ContentLength = file.Length;
        if (!HttpMethods.IsHead(Request.Method)) await file.CopyToAsync(Response.Body, Ct);
        return null;
    }

    // head, when given, receives the first bytes of the stream once (for MusicGain). reopen, when given, picks
    // the copy up again after the source drops: the catalog sometimes cuts a long download partway through.
    private async Task CopyAsync(Stream input, Stream output, long limit, CancellationToken ct, Action<byte[]>? head = null,
        Func<long, Task<Stream?>>? reopen = null)
    {
        var resumes = 0;
        var buffer = new byte[65536];
        long total = 0;
        var start = head is null ? null : new MemoryStream();
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                int read;
                try { read = await input.ReadAsync(buffer, idle.Token); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested
                    && reopen is not null && resumes < 3)
                {
                    resumes++;
                    logger.LogInformation("StreamFin Subsonic: the catalog dropped a song after {Bytes} bytes, resuming", total);
                    input = await reopen(total) ?? throw new IOException("The catalog could not resume the song.", ex);
                    continue;
                }
                start?.Write(buffer, 0, (int)Math.Min(read, MusicGain.HeadBytes - start.Length));
                if (start is { Length: >= MusicGain.HeadBytes })
                {
                    head!(start.ToArray());
                    start = null;
                }
                if (read == 0) return;
                total += read;
                // Access is checked again as the bytes flow, so turning a user off stops a stream already running.
                if (total > limit || !Plugin.Allows(user.Id) || !SubsonicAuth.Enabled) return;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        // A player that hangs up early, or a short range, still taught us whatever tags it covered.
        finally { if (start is { Length: > 0 }) head!(start.ToArray()); }
    }

    public enum AudioFormat { Raw, Mp3, Opus }

    // FLAC goes out untouched unless the client asks for a lossy format or a bit rate a FLAC cannot meet.
    // With preferLossless, a lossy format only counts when it comes with a real bit rate limit: apps such as
    // Amperfy ask for MP3 by default, which costs quality and makes every seek a fresh conversion.
    public static (AudioFormat Format, int Kbps) Choose(string? format, string? maxBitRate, bool preferLossless = false)
    {
        var max = int.TryParse(maxBitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 0;
        if (preferLossless && max is 0 or >= 1000) return (AudioFormat.Raw, 0);
        var kind = (format ?? "").ToLowerInvariant() switch
        {
            "raw" => AudioFormat.Raw,
            "" or "flac" => max is > 0 and < 1000 ? AudioFormat.Mp3 : AudioFormat.Raw,
            "opus" or "ogg" or "oga" => AudioFormat.Opus,
            _ => AudioFormat.Mp3
        };
        return kind switch
        {
            AudioFormat.Mp3 => (kind, Math.Clamp(max > 0 ? max : 320, 64, 320)),
            AudioFormat.Opus => (kind, Math.Clamp(max > 0 ? max : 160, 32, 256)),
            _ => (kind, 0)
        };
    }

    // Headers from the catalog within this. On a bad day it takes 20-40 s and Cloudflare answers 522 at about 40 s,
    // so a shorter limit would refuse songs that would have played. A song itself may take minutes.
    private static readonly TimeSpan HeaderLimit = TimeSpan.FromSeconds(45);

    private async Task<SubsonicNode?> StreamAsync(bool download)
    {
        var id = One("id");
        MusicTrack track;
        try { track = (await TrackAsync(id)).Track; }
        // The song itself comes from another address than its details, and on a bad day one answers while the other
        // does not. It plays with only its length and name unknown; when it is not reachable either, that error shows.
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException
            && ParseTrack(id) is var (releaseId, trackId))
        {
            track = new MusicTrack(trackId, "Song", [], releaseId, null, null, null, false, null, null);
        }
        // A clean edit reached from a list or a playlist plays the explicit recording when there is one. The lookup
        // gets a few seconds: a song that has to wait on a slow catalog is worse than the edit.
        var recording = MusicCatalog.PreferExplicit ? await catalog.ExplicitAsync(track, Ct, TimeSpan.FromSeconds(4)) : track;
        AudioTicket ticket;
        try { ticket = await sources.ResolveAsync(recording, Ct); }
        catch (ProviderException ex) { throw new SubsonicError(0, ex.Message); }
        if (!download) prefetch.After(user.Id, track);
        var upstream = ticket.Upstream;
        var flac = ticket.Lossless;
        var (format, kbps) = download ? (AudioFormat.Raw, 0) : Choose(One("format"), One("maxBitRate"), Plugin.Instance?.Configuration.SubsonicPreferLossless != false);
        if (format == AudioFormat.Raw)
        {
            var range = Request.Headers.Range.ToString();
            if (range.Length > 0 && !(System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1))
            {
                Response.StatusCode = 416;
                return null;
            }
            // Header values must be ASCII; filename* carries the real title for "Beyoncé" and friends.
            if (download) Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = "\"" + new string(MusicPaths.Part(track.Title).Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_').ToArray()) + "." + ticket.Container + "\"",
                FileNameStar = MusicPaths.Part(track.Title) + "." + ticket.Container
            }.ToString();
            Response.Headers.Vary = "Origin";
            // Gain is kept in memory only, so after a restart a song that is already on disk would never learn it:
            // it is served from the file without a download, which is where the gain is otherwise read.
            if (MusicGain.For(ticket.TrackId) is null && songs.OpenComplete(ticket.TrackId, ticket.Container) is { } cached)
            {
                await using (cached)
                {
                    var head = new byte[(int)Math.Min(MusicGain.HeadBytes, cached.Length)];
                    var read = await cached.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, Ct);
                    MusicGain.Remember(head.AsSpan(0, read), ticket.TrackId);
                }
            }
            songs.Warm(ticket);
            if (await songs.ServeAsync(HttpContext, ticket, range, () => Plugin.Allows(user.Id) && SubsonicAuth.Enabled)) return null;
            if (ticket.Local)
            {
                MusicAudio.Forget(ticket.TrackId);
                throw new SubsonicError(0, "This song was just removed from the server's cache. Play it again.");
            }
            using var response = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, range.Length == 0 ? null : range, HttpMethods.IsHead(Request.Method)),
                Ct, attempts: 2, outages: true);
            if ((int)response.StatusCode is not (200 or 206 or 416))
            {
                if (ticket.Source == "youtube") MusicAudio.Failed(ticket);
                throw new SubsonicError(0, ticket.Lossless ? "The music catalog could not stream this song." : "YouTube could not stream this song.");
            }
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = ticket.ContentType;
            Response.Headers.AcceptRanges = "bytes";
            Response.Headers.Vary = "Origin";
            if (response.Content.Headers.ContentLength is long length) Response.ContentLength = length;
            if (response.Content.Headers.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange.ToString();
            if (HttpMethods.IsHead(Request.Method)) return null;
            await using var input = await response.Content.ReadAsStreamAsync(Ct);
            var fromStart = response.StatusCode == System.Net.HttpStatusCode.OK || response.Content.Headers.ContentRange is { From: 0 };
            var size = response.Content.Headers.ContentRange?.Length ?? (response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
            var firstByte = response.Content.Headers.ContentRange?.From ?? 0;
            var lastByte = response.Content.Headers.ContentRange?.To ?? (size is long whole ? whole - 1 : (long?)null);
            var resumed = new List<HttpResponseMessage>();
            async Task<Stream?> Reopen(long done)
            {
                if (lastByte is null || firstByte + done > lastByte) return null;
                var from = firstByte + done;
                var again = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, $"bytes={from}-{lastByte}"), Ct, attempts: 3, outages: true);
                resumed.Add(again);
                // Anything but the exact bytes asked for would splice the wrong audio into the song.
                if (again.StatusCode == System.Net.HttpStatusCode.PartialContent && again.Content.Headers.ContentRange?.From == from)
                    return await again.Content.ReadAsStreamAsync(Ct);
                logger.LogWarning("StreamFin Subsonic could not resume a song at byte {Byte}: the catalog answered {Status}", from, (int)again.StatusCode);
                return null;
            }
            try { await CopyAsync(input, Response.Body, long.MaxValue, Ct, fromStart && flac ? bytes => KeepHead(upstream, bytes, size, [track.Id, recording.Id]) : null, Reopen); }
            finally { foreach (var again in resumed) again.Dispose(); }
            return null;
        }
        // A player that hangs up on one range usually opens the next at once, so wait a little for its slot.
        for (var waited = 0; UserTranscodes.AddOrUpdate(user.Id, 1, (_, n) => n + 1) > UserTranscodeLimit; waited++)
        {
            UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1);
            if (waited >= 20) throw new SubsonicError(0, "Too many songs are being converted for you at once. Try again in a moment.");
            await Task.Delay(500, Ct);
        }
        try { await TranscodeAsync(ticket, format, kbps, recording.DurationMs, [track.Id, recording.Id]); }
        finally { UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1); }
        return null;
    }

    // FFmpeg reads the FLAC from a pipe that this method fills through SafeHttp, so FFmpeg never opens a
    // network connection of its own. Its output is a pipe as well.
    // A single byte range against a file of the given size: (first, last), or null when the header is not one.
    public static (long First, long Last)? ParseRange(string header, long size)
    {
        if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(header, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1) return null;
        var (from, to) = (range.Ranges.First().From, range.Ranges.First().To);
        if (from is null) return to is > 0 ? (Math.Max(0, size - to.Value), size - 1) : null;
        return (from.Value, Math.Min(to ?? size - 1, size - 1));
    }

    // MP3 at a constant bit rate is exactly kbps * 125 bytes a second, so its size is known before it exists and
    // a byte offset maps to a time. That lets it answer ranges, which Apple's player insists on: without them
    // iOS apps such as Amperfy silently refuse to play a converted song.
    private async Task TranscodeAsync(AudioTicket ticket, AudioFormat format, int kbps, int? durationMs, string[] ids)
    {
        var offset = Int("timeOffset", 0, 0, 24 * 3600);
        var upstream = ticket.Upstream;
        songs.Warm(ticket);
        long? size = format == AudioFormat.Mp3 && durationMs is > 0 && offset == 0 ? (long)(durationMs.Value / 1000.0 * kbps * 125) : null;
        long first = 0;
        long? promised = One("estimateContentLength") == "true" && durationMs is > 0 ? (long)Math.Max(0, (durationMs.Value / 1000.0 - offset) * kbps * 125) : null;
        Response.ContentType = format == AudioFormat.Opus ? "audio/ogg" : "audio/mpeg";
        if (size is long total)
        {
            Response.Headers.AcceptRanges = "bytes";
            promised = total;
            if (Request.Headers.Range.ToString() is { Length: > 0 } header)
            {
                if (ParseRange(header, total) is not var (from, to) || from >= total || to < from)
                {
                    Response.StatusCode = 416;
                    Response.Headers.ContentRange = $"bytes */{total}";
                    return;
                }
                (first, promised) = (from, to - from + 1);
                Response.StatusCode = 206;
                Response.Headers.ContentRange = $"bytes {from}-{to}/{total}";
            }
        }
        Response.ContentLength = promised;
        if (HttpMethods.IsHead(Request.Method)) return;
        // Apple's player first asks for bytes 0-1. Every MP3 this makes starts with the same frame sync
        // (MPEG-1 layer III, no CRC, no ID3 or Xing header), so the probe needs neither the catalog nor FFmpeg.
        if (size is not null && first == 0 && promised is <= 2)
        {
            await Response.Body.WriteAsync(new byte[] { 0xFF, 0xFB }.AsMemory(0, (int)promised.Value), Ct);
            return;
        }
        var seek = offset + first / (kbps * 125.0);
        if (!await Transcodes.WaitAsync(TimeSpan.FromSeconds(10), Ct)) throw new SubsonicError(0, "The server is busy converting other songs. Try again shortly.");
        try
        {
            // A seek reads the FLAC header, then only the audio from a little before the wanted moment: FLAC frames
            // stand alone and carry their own position, so FFmpeg picks up at the next frame. Reading from the
            // start instead would fetch half of a 100 MB hi-res file to seek to its middle.
            byte[]? header = null;
            string? from = null;
            // A song already on disk feeds FFmpeg from the file: no catalog request at all.
            await using var local = songs.OpenComplete(ticket.TrackId, ticket.Container);
            if (local is null && ticket.Local) throw new SubsonicError(0, "This song was just removed from the server's cache. Play it again.");
            // Only FLAC can start reading partway through; anything else is decoded from its start and trimmed.
            if (local is not null && ticket.Lossless)
            {
                if (seek > 0 && durationMs is > 0)
                {
                    var bytes = new byte[MusicGain.HeadBytes];
                    var have = await local.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, Ct);
                    if (MusicGain.AudioStart(bytes.AsSpan(0, have)) is int audio && audio < have)
                    {
                        header = bytes[..audio];
                        local.Position = audio + (long)((local.Length - audio) * Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9);
                    }
                    else local.Position = 0;
                }
            }
            else if (ticket.Lossless && seek > 0 && durationMs is > 0 && FlacHeads.TryGetValue<FlacHead>(upstream.AbsoluteUri, out var known) && known is not null)
            {
                header = known.Header;
                from = $"bytes={header.Length + (long)((known.Size - header.Length) * Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9)}-";
            }
            else if (ticket.Lossless && local is null && seek > 0 && durationMs is > 0)
            {
                using var head = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, "bytes=0-65535"), Ct, attempts: 2, outages: true);
                if (head.StatusCode == System.Net.HttpStatusCode.PartialContent && head.Content.Headers.ContentRange?.Length is long flacSize)
                {
                    await using var headStream = await head.Content.ReadAsStreamAsync(Ct);
                    var bytes = new byte[MusicGain.HeadBytes];
                    var have = 0;
                    for (int got; have < bytes.Length && (got = await headStream.ReadAsync(bytes.AsMemory(have), Ct)) > 0;) have += got;
                    if (MusicGain.AudioStart(bytes.AsSpan(0, have)) is int audio && audio < have)
                    {
                        KeepHead(upstream, bytes[..have], flacSize, ids);
                        header = bytes[..audio];
                        // Bit rates vary along a song, so start early; the trim below finds the exact moment.
                        var fraction = Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9;
                        from = $"bytes={audio + (long)((flacSize - audio) * fraction)}-";
                    }
                }
            }
            using var response = local is not null ? null : await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, from), Ct, attempts: 2, outages: true);
            if (response is { IsSuccessStatusCode: false }) throw new SubsonicError(0, ticket.Lossless ? "The music catalog could not stream this song." : "YouTube could not stream this song.");
            var start = new ProcessStartInfo(encoder.EncoderPath)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            // -copyts keeps the song's own timestamps, so the trim cuts at the real moment wherever the bytes began.
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-copyts", "-f", ticket.Lossless ? "flac" : "mp4", "-i", "pipe:0", "-map", "0:a:0", "-map_metadata", "-1", "-vn" })
                start.ArgumentList.Add(arg);
            if (seek > 0) { start.ArgumentList.Add("-af"); start.ArgumentList.Add($"atrim=start={seek.ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS"); }
            foreach (var arg in format == AudioFormat.Opus
                         ? new[] { "-c:a", "libopus", "-b:a", kbps + "k", "-f", "ogg", "pipe:1" }
                         : new[] { "-c:a", "libmp3lame", "-b:a", kbps + "k", "-ar", "44100", "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3", "pipe:1" })
                start.ArgumentList.Add(arg);
            Process? started;
            try { started = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { started = null; }
            using var process = started ?? throw new SubsonicError(0, "FFmpeg could not be started.");
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            // Only FFmpeg's last words are kept: a corrupt stream can make it complain for every frame.
            var errors = YtDlp.TailAsync(process.StandardError, abort.Token);
            var feed = Task.Run(async () =>
            {
                try
                {
                    if (header is not null) await process.StandardInput.BaseStream.WriteAsync(header, abort.Token);
                    if (response is null) await CopyAsync(local!, process.StandardInput.BaseStream, 2L * 1024 * 1024 * 1024, abort.Token);
                    else
                    {
                        await using var input = await response.Content.ReadAsStreamAsync(abort.Token);
                        var size = response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null;
                        await CopyAsync(input, process.StandardInput.BaseStream, 2L * 1024 * 1024 * 1024, abort.Token, header is null && ticket.Lossless ? bytes => KeepHead(upstream, bytes, size, ids) : null);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { }
                finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            }, abort.Token);
            try
            {
                var buffer = new byte[32768];
                int read;
                long sent = 0;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, abort.Token)) > 0)
                {
                    if (!Plugin.Allows(user.Id) || !SubsonicAuth.Enabled) break;
                    // An estimated length is a promise Kestrel enforces: the last few bytes past it are dropped.
                    if (promised is long cap) read = (int)Math.Min(read, cap - sent);
                    if (read <= 0) break;
                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), abort.Token);
                    sent += read;
                }
                if (sent == 0 && !Response.HasStarted) throw new SubsonicError(0, "This song could not be converted.");
                // Short of the promise: decoders skip zero bytes while looking for the next frame.
                if (read == 0 && promised is long owed && (sent > owed / 2 || first > 0 && sent > 0))
                {
                    Array.Clear(buffer);
                    for (; sent < owed; sent += buffer.Length) await Response.Body.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, owed - sent)), abort.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            finally
            {
                abort.Cancel();
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                try { await feed; } catch (OperationCanceledException) { }
                try { if (await errors is { Length: > 0 } text && process.ExitCode is not (0 or 255 or -9 or 137)) logger.LogWarning("StreamFin Subsonic transcode: {Error}", text.Length > 300 ? text[..300] : text); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
        }
        finally { Transcodes.Release(); }
    }
}
