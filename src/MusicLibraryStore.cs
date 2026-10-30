using System.Collections.Concurrent;
using System.Text.Json;

namespace StreamFin;

public sealed record MusicPlay(MusicTrack Track, string Album, DateTime At);
// Albums are kept without their track list; a listing loads the release again.
public sealed record MusicStarredAlbum(MusicRelease Release, DateTime At);
public sealed record MusicStarredArtist(MusicArtistRef Artist, DateTime At);
// An album being kept as files in the music library folder (MusicSaver); State is one of MusicSaver's constants.
public sealed record MusicSave(string ReleaseId, string Title, string Artist, string State, int Saved, int Total, string? Note, DateTime Queued,
    DateTime? Finished = null);
public sealed record MusicPlaylist(Guid Id, string Name, string Comment, DateTime Created, DateTime Changed, IReadOnlyList<MusicPlay> Entries);
// What an app was playing, so another device can pick it up (Subsonic savePlayQueue). Current is a song id.
public sealed record MusicQueue(IReadOnlyList<MusicPlay> Entries, string? Current, long Position, DateTime Changed, string ChangedBy);
public sealed record MusicBookmark(MusicPlay Entry, long Position, string Comment, DateTime Created, DateTime Changed);
public sealed class MusicLibrary
{
    public List<MusicPlay> Plays { get; set; } = [];
    public List<MusicPlay> StarredTracks { get; set; } = [];
    public List<MusicStarredAlbum> StarredAlbums { get; set; } = [];
    public List<MusicStarredArtist> StarredArtists { get; set; } = [];
    public List<MusicPlaylist> Playlists { get; set; } = [];
    public List<MusicSave> Saves { get; set; } = [];
    // 1 to 5 stars, keyed by Subsonic id (tr-, al-, ar-).
    public Dictionary<string, int> Ratings { get; set; } = [];
    public MusicQueue? Queue { get; set; }
    public List<MusicBookmark> Bookmarks { get; set; } = [];
    // When anything here last changed, for apps that ask whether their copy of the index is current.
    public DateTime Changed { get; set; }
}

// The catalog has no accounts, so what a user played lives here, one JSON file per Jellyfin user.
public sealed class MusicLibraryStore
{
    public const int PlayLimit = 500;
    public const int StarLimit = 5000;
    public const int PlaylistLimit = 200;
    public const int PlaylistEntryLimit = 2000;
    // Across all of a user's playlists, so the file stays a few megabytes at most.
    public const int PlaylistTotalLimit = 20_000;
    public const int SaveLimit = 1000;
    public const int QueueLimit = 1000;
    public const int BookmarkLimit = 200;
    public static bool IsRatingKey(string? id) => id is not null
        && System.Text.RegularExpressions.Regex.IsMatch(id, @"\A(?:tr-[0-9]{15,20}-[0-9]{15,20}|al-[0-9]{15,20}|ar-[0-9]{15,20})\z",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
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

    // content: whether this changes what the library lists. A saved play queue does not, so apps that compare
    // the index date do not sync again on every skip.
    public async Task UpdateAsync(Guid user, Action<MusicLibrary> change, CancellationToken ct, bool content = true)
    {
        if (user == Guid.Empty) throw new ArgumentException("A user is required.", nameof(user));
        var gate = gates.GetOrAdd(user, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var library = Copy(await LoadAsync(user, ct));
            change(library);
            library.Plays = library.Plays.OrderByDescending(p => p.At).Take(PlayLimit).ToList();
            library.StarredTracks = library.StarredTracks.DistinctBy(s => s.Track.Id).Take(StarLimit).ToList();
            library.StarredAlbums = library.StarredAlbums.DistinctBy(s => s.Release.Id).Take(StarLimit).ToList();
            library.StarredArtists = library.StarredArtists.DistinctBy(s => s.Artist.Id).Take(StarLimit).ToList();
            library.Saves = library.Saves.DistinctBy(s => s.ReleaseId).Take(SaveLimit).Select(s => s with { Title = Bounded(s.Title), Artist = Bounded(s.Artist),
                Note = s.Note is null ? null : Bounded(s.Note) }).ToList();
            library.Ratings = library.Ratings.Where(r => r.Value is >= 1 and <= 5 && IsRatingKey(r.Key)).Take(StarLimit).ToDictionary();
            if (library.Queue is { } queue)
                library.Queue = queue with { Entries = queue.Entries.Take(QueueLimit).ToArray(), ChangedBy = Bounded(queue.ChangedBy), Position = Math.Max(0, queue.Position) };
            library.Bookmarks = library.Bookmarks.DistinctBy(b => b.Entry.Track.Id).Take(BookmarkLimit)
                .Select(b => b with { Comment = Bounded(b.Comment), Position = Math.Max(0, b.Position) }).ToList();
            if (content) library.Changed = DateTime.UtcNow;
            var room = PlaylistTotalLimit;
            library.Playlists = library.Playlists.DistinctBy(p => p.Id).Take(PlaylistLimit).Select(p =>
            {
                var entries = p.Entries.Take(Math.Min(PlaylistEntryLimit, room)).ToArray();
                room -= entries.Length;
                return p with { Name = Bounded(p.Name), Comment = Bounded(p.Comment), Entries = entries };
            }).ToList();
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
        static bool Valid(MusicPlay? p) => p?.Track is { } t && MusicCatalog.IsId(t.Id) && MusicCatalog.IsId(t.ReleaseId) && t.Artists is not null && p.Album is not null;
        library.Plays = (library.Plays ?? []).Where(Valid).ToList();
        library.StarredTracks = (library.StarredTracks ?? []).Where(Valid).ToList();
        library.StarredAlbums = (library.StarredAlbums ?? []).Where(s => s?.Release is { Title: not null, Artists: not null } r && MusicCatalog.IsId(r.Id))
            .Select(s => s with { Release = s.Release with { Tracks = [] } }).ToList();
        library.StarredArtists = (library.StarredArtists ?? []).Where(s => s?.Artist is { Name: not null } a && MusicCatalog.IsId(a.Id)).ToList();
        library.Saves = (library.Saves ?? []).Where(s => s is { Title: not null, Artist: not null, State: MusicSaver.Queued or MusicSaver.Saving or MusicSaver.Saved or MusicSaver.Failed }
            && MusicCatalog.IsId(s.ReleaseId)).ToList();
        library.Playlists = (library.Playlists ?? []).Where(p => p is { Name: not null } && p.Id != Guid.Empty)
            .Select(p => p with { Comment = p.Comment ?? "", Entries = (p.Entries ?? []).Where(Valid).ToArray() }).ToList();
        library.Ratings = (library.Ratings ?? []).Where(r => r.Value is >= 1 and <= 5 && IsRatingKey(r.Key)).ToDictionary();
        library.Queue = library.Queue is { Entries: not null, ChangedBy: not null } queue
            ? queue with { Entries = queue.Entries.Where(Valid).ToArray(), Current = IsRatingKey(queue.Current) ? queue.Current : null } : null;
        library.Bookmarks = (library.Bookmarks ?? []).Where(b => b is { Comment: not null } && Valid(b.Entry)).ToList();
        // A library from before this was kept counts as changed now, so an app's older index date is not taken as current.
        if (library.Changed == default) library.Changed = DateTime.UtcNow;
        return loaded[user] = library;
    }

    private static string Bounded(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length > 200 ? trimmed[..200] : trimmed;
    }

    private static MusicLibrary Copy(MusicLibrary library) => new()
    {
        Plays = [.. library.Plays], StarredTracks = [.. library.StarredTracks], StarredAlbums = [.. library.StarredAlbums],
        StarredArtists = [.. library.StarredArtists], Playlists = [.. library.Playlists], Saves = [.. library.Saves],
        Ratings = new(library.Ratings), Queue = library.Queue, Bookmarks = [.. library.Bookmarks], Changed = library.Changed
    };
}
