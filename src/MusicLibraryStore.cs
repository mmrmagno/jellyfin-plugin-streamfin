using System.Collections.Concurrent;
using System.Text.Json;

namespace StreamFin;

public sealed record MusicPlay(MusicTrack Track, string Album, DateTime At);
public sealed class MusicLibrary
{
    public List<MusicPlay> Plays { get; set; } = [];
}

// The catalog has no accounts, so what a user played lives here, one JSON file per Jellyfin user.
public sealed class MusicLibraryStore
{
    public const int PlayLimit = 500;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    private readonly ConcurrentDictionary<Guid, MusicLibrary> loaded = new();
    private readonly ConcurrentDictionary<Guid, long> versions = new();
    private readonly string? fixedRoot;
    public MusicLibraryStore() { }
    public MusicLibraryStore(string root) => fixedRoot = root;
    private string? Root => fixedRoot ?? (Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "music") : null);

    private string? File(Guid user) => Root is { } root ? Path.Combine(root, user.ToString("N") + ".json") : null;

    public async Task<MusicLibrary> ReadAsync(Guid user, CancellationToken ct)
    {
        var gate = gates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return Copy(await LoadAsync(user, ct)); }
        finally { gate.Release(); }
    }

    public async Task UpdateAsync(Guid user, Action<MusicLibrary> change, CancellationToken ct)
    {
        if (user == Guid.Empty) throw new ArgumentException("A user is required.", nameof(user));
        var gate = gates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var library = Copy(await LoadAsync(user, ct));
            change(library);
            library.Plays = library.Plays.OrderByDescending(p => p.At).Take(PlayLimit).ToList();
            if (File(user) is { } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                await System.IO.File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(library), ct);
                System.IO.File.Move(temporary, path, overwrite: true);
            }
            loaded[user] = library;
            versions.AddOrUpdate(user, 1, (_, version) => version + 1);
        }
        finally { gate.Release(); }
    }

    // Changes on every write in this process; enough to tell Jellyfin a cached listing is stale.
    public long Version(Guid user) => versions.GetValueOrDefault(user);

    public Task RecordPlayAsync(Guid user, MusicTrack track, string album, CancellationToken ct)
        => UpdateAsync(user, library => library.Plays.Insert(0, new MusicPlay(track, album, DateTime.UtcNow)), ct);

    // Most recent first, one entry per track.
    public static IReadOnlyList<MusicPlay> Recent(MusicLibrary library, int count) => library.Plays.OrderByDescending(p => p.At)
        .DistinctBy(p => p.Track.Id).Take(count).ToArray();

    private async Task<MusicLibrary> LoadAsync(Guid user, CancellationToken ct)
    {
        if (loaded.TryGetValue(user, out var known)) return known;
        var library = new MusicLibrary();
        if (File(user) is { } path && System.IO.File.Exists(path))
        {
            // A damaged file must not take music down; it is replaced on the next change.
            try { library = JsonSerializer.Deserialize<MusicLibrary>(await System.IO.File.ReadAllBytesAsync(path, ct)) ?? library; }
            catch (JsonException) { }
        }
        library.Plays = library.Plays.Where(p => p?.Track is { } t && MusicCatalog.IsId(t.Id) && MusicCatalog.IsId(t.ReleaseId)).ToList();
        return loaded[user] = library;
    }

    private static MusicLibrary Copy(MusicLibrary library) => new() { Plays = [.. library.Plays] };
}
