using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Cover art kept on disk: apps load the same covers on every screen, and they stay visible while the catalog is
// down. Files are named by a hash of the artwork address (which the catalog parsers already limit to the catalog
// host), and only JPEG, PNG and WebP are kept, recognised by their first bytes rather than by what the server says.
public sealed class MusicArtCache(SafeHttp http, ILogger<MusicArtCache> logger)
{
    public const int ImageLimit = 10 * 1024 * 1024;
    public const long ByteLimit = 300L * 1024 * 1024;
    private readonly string? fixedRoot;

    public MusicArtCache(SafeHttp http, ILogger<MusicArtCache> logger, string root) : this(http, logger) => fixedRoot = root;

    private string? Root => fixedRoot ?? (Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "music-art") : null);

    public static string? Type(ReadOnlySpan<byte> bytes) => bytes switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => "image/png",
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
        _ => null
    };

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> fetching = new();
    // A screen of covers asks for dozens at once; the catalog and the server get a few at a time.
    private readonly SemaphoreSlim fetches = new(4, 4);
    private long written;

    private string? File(string address) => Root is { } root
        ? Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address))))
        : null;

    // The kept file and its type, or null when it is neither kept nor obtainable now. down skips the fetch while
    // the catalog is out. Requests for the same cover share one fetch, written to disk as it arrives.
    public async Task<(string Path, string Type)?> GetAsync(string address, bool down, CancellationToken ct)
    {
        if (File(address) is not { } path) return null;
        if (Kept(path) is { } type) return (path, type);
        if (down) return null;
        var shared = fetching.GetOrAdd(path, name =>
        {
            var call = Task.Run(() => FetchAsync(address, path));
            _ = call.ContinueWith(done => fetching.TryRemove(new KeyValuePair<string, Task>(path, done)), TaskScheduler.Default);
            return call;
        });
        if (shared.IsCompleted) fetching.TryRemove(new KeyValuePair<string, Task>(path, shared));
        await shared.WaitAsync(ct);
        return Kept(path) is { } fetched ? (path, fetched) : null;
    }

    private static string? Kept(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > ImageLimit) return null;
            var head = new byte[12];
            string? type;
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                type = Type(head.AsSpan(0, file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)));
            if (type is not null && DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(1)) System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return type;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task FetchAsync(string address, string path)
    {
        if (!await fetches.WaitAsync(TimeSpan.FromSeconds(15))) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long size = 0;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await http.OpenAsync(new Uri(address), false, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > ImageLimit) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                for (int read; (read = await input.ReadAsync(buffer, timeout.Token)) > 0;)
                {
                    if ((size += read) > ImageLimit) return;
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
            }
            if (Kept(temporary) is null) return;
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ProviderException or UriFormatException or UnauthorizedAccessException
            or OperationCanceledException)
        {
            logger.LogInformation("StreamFin cover art not kept ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            fetches.Release();
            try { System.IO.File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (Interlocked.Add(ref written, size) > 30 * 1024 * 1024)
        {
            Interlocked.Exchange(ref written, 0);
            Trim();
        }
    }

    public void Trim()
    {
        if (Root is not { } root || !Directory.Exists(root)) return;
        try
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(root).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc))
            {
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal)) { if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromHours(1)) file.Delete(); continue; }
                if ((total += file.Length) > ByteLimit) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogInformation("StreamFin cover art could not be trimmed ({ErrorType})", ex.GetType().Name); }
    }
}
