using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Songs kept on the server's disk. The first time a song is asked for, it is fetched whole in the background (one
// catalog request instead of one per skip) while the player is served from the catalog as before. Once the part a
// player wants is on disk, every range, skip and replay comes from the file. The least recently played songs make
// room for new ones. Only the catalog's own /track/<id> addresses are kept, named by their validated id.
public sealed class MusicCache(SafeHttp http, ILogger<MusicCache> logger)
{
    public const long SongLimit = 400L * 1024 * 1024;
    private const int Downloads = 2;
    // A request this far past what has been fetched goes to the catalog instead of waiting.
    private const long Ahead = 2 * 1024 * 1024;

    private sealed class Entry
    {
        public required string Path { get; init; }
        public long Size;
        public long Written;
        public volatile bool Complete;
        public DateTime Used;
        public CancellationTokenSource? Stop;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new();
    private static readonly object Gate = new();
    private static string? scanned;
    // After the catalog says "too many requests", new downloads wait instead of adding to it.
    private static DateTime coolUntil;
    private readonly string? fixedRoot;

    public MusicCache(SafeHttp http, ILogger<MusicCache> logger, string root) : this(http, logger) => fixedRoot = root;

    private static long Capacity => Math.Clamp(Plugin.Instance?.Configuration.MusicCacheMb ?? 0, 0, 1_000_000) * 1024L * 1024;
    private string? Root => fixedRoot ?? (Plugin.Instance is { } plugin ? System.IO.Path.Combine(plugin.DataFolderPath, "music-cache") : null);
    private long Limit => fixedRoot is null ? Capacity : 64L * 1024 * 1024;

    public static string? Key(Uri upstream) => MusicCatalog.Api is { } api && upstream.Scheme == Uri.UriSchemeHttps && upstream.IdnHost == api.IdnHost
        && upstream.AbsolutePath.StartsWith("/track/", StringComparison.Ordinal) && MusicCatalog.IsId(upstream.AbsolutePath[7..]) ? upstream.AbsolutePath[7..] : null;

    // The real size of a song once its download has begun, for apps that list it (Amperfy hides songs without one).
    public static long? Size(string trackId) => Entries.TryGetValue(trackId, out var entry) && entry.Size > 0 ? entry.Size : null;

    public void Warm(Uri upstream)
    {
        if (Limit == 0 || Root is not { } root || Key(upstream) is not { } id) return;
        lock (Gate)
        {
            Scan(root);
            if (!Entries.ContainsKey(id) && DateTime.UtcNow < coolUntil) return;
            if (Entries.TryGetValue(id, out var known))
            {
                known.Used = DateTime.UtcNow;
                // The file's age is what orders eviction after a restart.
                if (known.Complete)
                {
                    try { File.SetLastWriteTimeUtc(known.Path, known.Used); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    if (MusicGain.For(id) is null) Learn(id, known.Path);
                }
                return;
            }
            var running = Entries.Values.Where(e => !e.Complete).ToList();
            if (running.Count >= Downloads)
            {
                // Someone skipped on: a download nobody has read from for a while gives way to the new song.
                if (running.Where(e => DateTime.UtcNow - e.Used > TimeSpan.FromSeconds(15)).MinBy(e => e.Used) is not { } idle) return;
                idle.Stop?.Cancel();
            }
            var entry = new Entry { Path = System.IO.Path.Combine(root, id + ".flac"), Used = DateTime.UtcNow, Stop = new CancellationTokenSource(TimeSpan.FromMinutes(15)) };
            Entries[id] = entry;
            _ = Task.Run(() => DownloadAsync(id, upstream, entry));
        }
    }

    // Whether the whole song is on disk, so it can play while the catalog is out.
    public bool Has(string trackId)
    {
        if (Limit == 0 || Root is not { } root) return false;
        lock (Gate) Scan(root);
        return Entries.TryGetValue(trackId, out var entry) && entry.Complete;
    }

    // The finished file, for FFmpeg's input; null while it is still being fetched.
    public FileStream? OpenComplete(Uri upstream)
    {
        if (Key(upstream) is not { } id || !Entries.TryGetValue(id, out var entry) || !entry.Complete) return null;
        try
        {
            entry.Used = DateTime.UtcNow;
            return new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        }
        catch (IOException) { return null; }
    }

    // Answers a request from disk when the song, or the part asked for, is there. Returns false, having written
    // nothing, when the caller should go to the catalog instead.
    public async Task<bool> ServeAsync(HttpContext context, Uri upstream, string range, Func<bool> allowed)
    {
        if (Key(upstream) is not { } id || !Entries.TryGetValue(id, out var entry)) return false;
        // A download that has started learns the song's size as soon as the catalog answers, which on a bad day takes
        // 20-40 s. Waiting for it (up to its own 45 s limit) beats a second request for the same song, which doubles
        // the load on a struggling catalog: the player then reads the file as it fills. A failed download leaves
        // the table at once, and the caller goes to the catalog itself.
        var size = Volatile.Read(ref entry.Size);
        for (var i = 0; size <= 0 && i < 1000 && Entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry); i++)
        {
            await Task.Delay(50, context.RequestAborted);
            size = Volatile.Read(ref entry.Size);
        }
        if (size <= 0) return false;
        long first = 0, last = size - 1;
        if (range.Length > 0)
        {
            if (SubsonicController.ParseRange(range, size) is not var (from, to) || to < from) return false;
            if (from >= size)
            {
                context.Response.StatusCode = 416;
                context.Response.Headers.ContentRange = $"bytes */{size}";
                return true;
            }
            (first, last) = (from, to);
        }
        if (!entry.Complete && first > Volatile.Read(ref entry.Written) + Ahead) return false;
        FileStream file;
        try { file = new FileStream(entry.Complete ? entry.Path : entry.Path + ".part", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true); }
        catch (IOException)
        {
            // It may have been renamed into place a moment ago.
            try { file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true); }
            catch (IOException) { return false; }
        }
        await using (file)
        {
            var response = context.Response;
            response.StatusCode = range.Length > 0 ? 206 : 200;
            response.ContentType = "audio/flac";
            response.Headers.AcceptRanges = "bytes";
            response.ContentLength = last - first + 1;
            if (range.Length > 0) response.Headers.ContentRange = $"bytes {first}-{last}/{size}";
            entry.Used = DateTime.UtcNow;
            if (HttpMethods.IsHead(context.Request.Method)) return true;
            var ct = context.RequestAborted;
            file.Position = first;
            var buffer = new byte[1 << 16];
            var remaining = last - first + 1;
            var waited = 0;
            try
            {
                while (remaining > 0)
                {
                    entry.Used = DateTime.UtcNow;
                    var available = entry.Complete ? size : Volatile.Read(ref entry.Written);
                    if (file.Position >= available)
                    {
                        // The download stopped before reaching this part: cut the connection rather than send a short file as whole.
                        // The wait outlasts the download's own 45 s stall limit, so a resume in progress gets its chance.
                        if (!Entries.TryGetValue(id, out var current) || !ReferenceEquals(current, entry) || ++waited > 1200) { context.Abort(); return true; }
                        await Task.Delay(50, ct);
                        continue;
                    }
                    waited = 0;
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, Math.Min(remaining, available - file.Position))), ct);
                    if (read == 0) { await Task.Delay(50, ct); continue; }
                    if (!allowed()) return true;
                    await response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Headers are out with the full length; a short body must not pass for the whole song.
                if (!ct.IsCancellationRequested) context.Abort();
            }
        }
        return true;
    }

    private async Task DownloadAsync(string id, Uri upstream, Entry entry)
    {
        var part = entry.Path + ".part";
        var ct = entry.Stop!.Token;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(part)!);
            await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            // The catalog drops long downloads partway and sometimes answers a resume with a 5xx. Only attempts that
            // gain nothing count against the song, so a slow, choppy download still finishes.
            for (int failures = 0, attempts = 0; entry.Written < entry.Size || entry.Size == 0; attempts++)
            {
                if (failures >= 3 || attempts >= 30) return;
                var from = Volatile.Read(ref entry.Written);
                if (from > 0 && failures > 0) await Task.Delay(TimeSpan.FromSeconds(2 * failures), ct);
                using var response = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, TimeSpan.FromSeconds(45), ct, from == 0 ? null : $"bytes={from}-"), ct,
                    outages: true);
                if (from == 0)
                {
                    if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not long size || size is <= 0 or > SongLimit)
                    {
                        logger.LogInformation("StreamFin music cache: {Id} not kept, the catalog answered {Status}", id, (int)response.StatusCode);
                        if (response.StatusCode == HttpStatusCode.TooManyRequests) coolUntil = DateTime.UtcNow.AddMinutes(2);
                        return;
                    }
                    Volatile.Write(ref entry.Size, size);
                }
                else if (response.StatusCode != HttpStatusCode.PartialContent || response.Content.Headers.ContentRange?.From != from)
                {
                    logger.LogInformation("StreamFin music cache: {Id} could not resume at byte {From}, the catalog answered {Status}", id, from, (int)response.StatusCode);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests) coolUntil = DateTime.UtcNow.AddMinutes(2);
                    failures++;
                    continue;
                }
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                try
                {
                    while (true)
                    {
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        idle.CancelAfter(TimeSpan.FromSeconds(45));
                        var read = await input.ReadAsync(buffer, idle.Token);
                        if (read == 0) break;
                        if (entry.Written + read > entry.Size) return;
                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        await file.FlushAsync(ct);
                        // Players read the song from this file, so its tags never pass the Subsonic stream; read them here.
                        if (Interlocked.Add(ref entry.Written, read) is var written && written >= MusicGain.HeadBytes && written - read < MusicGain.HeadBytes) Learn(id, part);
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException || ex is OperationCanceledException && !ct.IsCancellationRequested) { }
                if (Volatile.Read(ref entry.Written) == from) failures++;
                else failures = 0;
            }
            if (entry.Written != entry.Size) return;
            await file.DisposeAsync();
            var magic = new byte[4];
            await using (var check = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                if (await check.ReadAsync(magic, ct) != 4 || !magic.AsSpan().SequenceEqual("fLaC"u8)) return;
            File.Move(part, entry.Path, overwrite: true);
            entry.Complete = true;
            Learn(id, entry.Path);
            Trim();
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or ProviderException or UnauthorizedAccessException)
        {
            logger.LogInformation("StreamFin music cache: {Id} not kept ({ErrorType})", id, ex.GetType().Name);
        }
        finally
        {
            if (!entry.Complete)
            {
                Entries.TryRemove(new KeyValuePair<string, Entry>(id, entry));
                try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            lock (Gate)
            {
                entry.Stop?.Dispose();
                entry.Stop = null;
            }
        }
    }

    // ReplayGain from the file's own tags, so a song kept from before a restart is levelled too.
    private static void Learn(string id, string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[MusicGain.HeadBytes];
            MusicGain.Remember(head.AsSpan(0, file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)), id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // Oldest first, until the songs fit. A song played in the last minute stays, even over the limit.
    private void Trim()
    {
        lock (Gate)
        {
            var kept = Entries.Where(e => e.Value.Complete).OrderBy(e => e.Value.Used).ToList();
            var total = kept.Sum(e => e.Value.Size);
            foreach (var (id, entry) in kept)
            {
                if (total <= Limit) break;
                if (DateTime.UtcNow - entry.Used < TimeSpan.FromMinutes(1)) continue;
                if (!Entries.TryRemove(new KeyValuePair<string, Entry>(id, entry))) continue;
                try { File.Delete(entry.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                total -= entry.Size;
            }
        }
    }

    // What an earlier run left: finished songs are kept (their age orders eviction), half-fetched ones go.
    private void Scan(string root)
    {
        if (scanned == root) return;
        scanned = root;
        try
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.EnumerateFiles(root))
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.EndsWith(".flac", StringComparison.Ordinal) && MusicCatalog.IsId(name[..^5]))
                {
                    var info = new FileInfo(path);
                    Entries.TryAdd(name[..^5], new Entry { Path = path, Size = info.Length, Written = info.Length, Complete = true, Used = info.LastWriteTimeUtc });
                }
                else if (name.EndsWith(".part", StringComparison.Ordinal)) File.Delete(path);
            }
            Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("StreamFin music cache could not be read ({ErrorType})", ex.GetType().Name); }
    }
}
