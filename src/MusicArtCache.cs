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
        if (TypeOf(path) is { } type) return (path, type);
        if (down) return null;
        var shared = fetching.GetOrAdd(path, name =>
        {
            var call = Task.Run(() => FetchAsync(address, path));
            _ = call.ContinueWith(done => fetching.TryRemove(new KeyValuePair<string, Task>(path, done)), TaskScheduler.Default);
            return call;
        });
        if (shared.IsCompleted) fetching.TryRemove(new KeyValuePair<string, Task>(path, shared));
        await shared.WaitAsync(ct);
        return TypeOf(path) is { } fetched ? (path, fetched) : null;
    }

    // The image type of a file by its first bytes, or null when it is not an image this serves.
    public static string? TypeOf(string path)
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
            // The catalog serves covers through the same slow front as songs: 20-40 s on a bad day.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
            if (TypeOf(temporary) is null) return;
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

    // Apps ask for covers at the size they show them. Sizes are rounded up to a few steps, so each cover has a handful
    // of copies at most, made by Jellyfin's own image encoder a couple at a time.
    private static readonly int[] Steps = [64, 128, 256, 512, 1024];
    private readonly SemaphoreSlim resizes = new(2, 2);

    // A copy of source no wider or taller than size, or null when the original should be sent as it is.
    public async Task<string?> ResizedAsync(string source, int size, MediaBrowser.Controller.Drawing.IImageEncoder encoder)
    {
        if (Steps.FirstOrDefault(step => step >= size) is not (> 0 and var step) || Root is not { } root || !encoder.SupportsImageEncoding) return null;
        var path = Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source))) + "-" + step);
        if (TypeOf(path) is not null) return path;
        if (!await resizes.WaitAsync(TimeSpan.FromSeconds(10))) return null;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        // Jellyfin's encoder goes by the file's extension, which kept covers do not have.
        var input = TypeOf(source) switch { "image/jpeg" => temporary + ".jpg", "image/png" => temporary + ".png", "image/webp" => temporary + ".webp", _ => null };
        try
        {
            if (input is null) return null;
            Directory.CreateDirectory(root);
            System.IO.File.Copy(source, input);
            var options = new MediaBrowser.Controller.Drawing.ImageProcessingOptions { MaxWidth = step, MaxHeight = step, Quality = 85,
                SupportedOutputFormats = [MediaBrowser.Model.Drawing.ImageFormat.Jpg] };
            var made = await Task.Run(() => encoder.EncodeImage(input, System.IO.File.GetLastWriteTimeUtc(source), temporary, false, null, 85, options,
                MediaBrowser.Model.Drawing.ImageFormat.Jpg));
            // The encoder hands back the original when it is already small enough.
            if (made != temporary || TypeOf(temporary) is null) return null;
            System.IO.File.Move(temporary, path, overwrite: true);
            return path;
        }
        // Image libraries fail in many ways on odd files; the original cover still goes out.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogInformation("StreamFin cover art not resized ({ErrorType})", ex.GetType().Name);
            return null;
        }
        finally
        {
            resizes.Release();
            foreach (var leftover in new[] { temporary, input })
                try { if (leftover is not null) System.IO.File.Delete(leftover); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
                if (file.Name.Contains(".tmp", StringComparison.Ordinal)) { if (DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromHours(1)) file.Delete(); continue; }
                if ((total += file.Length) > ByteLimit) file.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogInformation("StreamFin cover art could not be trimmed ({ErrorType})", ex.GetType().Name); }
    }
}
