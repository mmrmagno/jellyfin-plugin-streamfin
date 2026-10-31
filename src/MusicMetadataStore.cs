using Microsoft.Extensions.Logging;

namespace StreamFin;

// The catalog's answers for releases and artists, kept on disk as it sent them, so albums, songs and lyrics
// still open when it is down, even right after a restart. Files are named by kind and validated id only, and
// read back through the same parsers as a live answer. The least recently used go once there are too many.
public sealed class MusicMetadataStore(ILogger<MusicMetadataStore> logger)
{
    public const int FileLimit = 20_000;
    public const int SizeLimit = 4 * 1024 * 1024;
    public const long ByteLimit = 200L * 1024 * 1024;
    private readonly string? fixedRoot;
    private int writes;
    private long written;

    public MusicMetadataStore(ILogger<MusicMetadataStore> logger, string root) : this(logger) => fixedRoot = root;

    private string? Root => fixedRoot ?? (Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "music-meta") : null);

    // Kept per catalog address: another instance's answers are not this one's.
    private string? File(string? kind, string? id)
    {
        if (kind is not ("release" or "artist") || !MusicCatalog.IsId(id) || Root is not { } root
            || (Deezer.IsId(id) ? Deezer.Host : MusicCatalog.ApiHost) is not { Length: > 0 } host) return null;
        var path = Path.GetFullPath(Path.Combine(root, host, kind, id + ".json"));
        return path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? path : null;
    }

    public bool Has(string kind, string id) => File(kind, id) is { } path && new FileInfo(path) is { Exists: true, Length: > 0 and <= SizeLimit };

    // A copy that no longer parses is dropped, so it stops counting as stored.
    public void Forget(string? kind, string? id)
    {
        if (File(kind, id) is not { } path) return;
        try { System.IO.File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public string? Read(string kind, string id)
    {
        if (File(kind, id) is not { } path) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > SizeLimit) return null;
            var text = System.IO.File.ReadAllText(path);
            // The file's age orders eviction; a read keeps it, but only once a day to spare the disk.
            if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(1)) System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task WriteAsync(string kind, string id, string json)
    {
        if (File(kind, id) is not { } path || json.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(json) is var bytes && bytes > SizeLimit) return;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(temporary, json);
            System.IO.File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation("StreamFin music metadata not kept ({ErrorType})", ex.GetType().Name);
            try { System.IO.File.Delete(temporary); } catch (Exception inner) when (inner is IOException or UnauthorizedAccessException) { }
            return;
        }
        // Trimmed every so many files or megabytes, whichever comes first, so the folder stays near its limits.
        if (Interlocked.Increment(ref writes) % 200 == 1 || Interlocked.Add(ref written, bytes) > 20 * 1024 * 1024)
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
            var files = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            // Leftovers of a write cut short by a restart.
            foreach (var stray in files.Where(f => f.Name.EndsWith(".tmp", StringComparison.Ordinal) && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromHours(1)))
                stray.Delete();
            long total = 0;
            var kept = 0;
            foreach (var file in files.Where(f => f.Name.EndsWith(".json", StringComparison.Ordinal)).OrderByDescending(f => f.LastWriteTimeUtc))
                if (++kept > FileLimit || (total += file.Length) > ByteLimit) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogInformation("StreamFin music metadata could not be trimmed ({ErrorType})", ex.GetType().Name); }
    }
}
