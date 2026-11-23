using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// Every play, kept per user and year (one JSON line each), so a yearly recap can count them. Recently played keeps
// only the last 500. Starts counting the day this file first appears; earlier plays are not known.
public sealed class MusicHistory(string? root = null)
{
    public const int YearLimit = 100_000;
    // A year of heavy listening is a few MB; the cap only stops a client that sends plays in a loop.
    public const long YearBytes = 40L * 1024 * 1024;
    // An app sends what it played offline in one batch, but never plays from years back or the future.
    public static DateTime Oldest => DateTime.UtcNow.AddDays(-366);
    public DateTime? Since { get; init; }
    public const int PlaysPerWindow = 1000;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private static readonly ConcurrentDictionary<Guid, (DateTime Start, int Count)> Budget = new();
    private static readonly Microsoft.Extensions.Caching.Memory.MemoryCache Recaps = new(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = 256 });

    // How many of these plays the user may still record in this window; the rest are dropped.
    private static int Allow(Guid user, int wanted)
    {
        var granted = 0;
        Budget.AddOrUpdate(user, _ => (DateTime.UtcNow, granted = Math.Min(wanted, PlaysPerWindow)), (_, current) =>
        {
            if (DateTime.UtcNow - current.Start > Window) current = (DateTime.UtcNow, 0);
            granted = Math.Clamp(PlaysPerWindow - current.Count, 0, wanted);
            return (current.Start, current.Count + granted);
        });
        return granted;
    }
    private string? Root => root ?? (Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "music", "history") : null);
    private string? Folder(Guid user) => Root is { } path ? Path.Combine(path, user.ToString("N")) : null;
    private string? File(Guid user, int year) => Folder(user) is { } folder ? Path.Combine(folder, year.ToString(CultureInfo.InvariantCulture) + ".jsonl") : null;

    public async Task AppendAsync(Guid user, IEnumerable<MusicPlay> plays, CancellationToken ct)
    {
        var oldest = Since ?? Oldest;
        var newest = DateTime.UtcNow.AddMinutes(5);
        var kept = plays.Where(p => p.Track.Title.Length > 0 && p.At >= oldest && p.At <= newest).ToList();
        kept = kept.Take(Allow(user, kept.Count)).ToList();
        foreach (var year in kept.GroupBy(p => p.At.Year))
        {
            if (File(user, year.Key) is not { } path) return;
            var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (System.IO.File.Exists(path) && new FileInfo(path).Length > YearBytes) continue;
                var lines = new StringBuilder();
                foreach (var play in year) lines.Append(JsonSerializer.Serialize(play)).Append('\n');
                await System.IO.File.AppendAllTextAsync(path, lines.ToString(), ct);
            }
            finally { gate.Release(); }
        }
    }

    public async Task<IReadOnlyList<MusicPlay>> ReadAsync(Guid user, int year, CancellationToken ct)
    {
        if (File(user, year) is not { } path || !System.IO.File.Exists(path)) return [];
        var plays = new List<MusicPlay>();
        // A damaged line (a crash mid-write) is skipped; the rest of the year still counts. Read line by line and only
        // up to the caps, so an oversized file never has to fit in memory whole.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        using var reader = new StreamReader(stream);
        while (plays.Count < YearLimit && stream.Position <= YearBytes && await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length > 64 * 1024) continue;
            try { if (JsonSerializer.Deserialize<MusicPlay>(line) is { Track.Title.Length: > 0 } play) plays.Add(play); }
            catch (JsonException) { }
        }
        return plays;
    }

    // A year's recap, kept until that year's file changes: apps ask for the playlists often.
    public async Task<MusicRecap> RecapAsync(Guid user, int year, CancellationToken ct)
    {
        var file = File(user, year) is { } path && System.IO.File.Exists(path) ? new FileInfo(path) : null;
        var key = (Root, user, year, file?.Length ?? 0, file?.LastWriteTimeUtc ?? default);
        if (Recaps.TryGetValue<MusicRecap>(key, out var known) && known is not null) return known;
        var recap = MusicRecap.Build(year, await ReadAsync(user, year, ct));
        Recaps.Set(key, recap, new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        return recap;
    }

    public IReadOnlyList<int> Years(Guid user) => Folder(user) is { } folder && Directory.Exists(folder)
        ? Directory.GetFiles(folder, "*.jsonl").Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 0)
            .Where(y => y is > 2000 and < 3000).OrderDescending().ToArray()
        : [];
}

public sealed record RecapSong(MusicTrack Track, string Album, int Plays);
public sealed record RecapArtist(MusicArtistRef Artist, string? Image, int Plays);
public sealed record RecapAlbum(string ReleaseId, string Title, IReadOnlyList<MusicArtistRef> Artists, string? Artwork, int Plays);
public sealed record MusicRecap(int Year, int Plays, int Minutes, int Songs, int Artists, string? TopMonth,
    IReadOnlyList<RecapSong> TopSongs, IReadOnlyList<RecapArtist> TopArtists, IReadOnlyList<RecapAlbum> TopAlbums)
{
    // Ties go to what was played first that year, so the order does not shuffle between listings.
    public static MusicRecap Build(int year, IEnumerable<MusicPlay> all, int top = 50)
    {
        var plays = all.Where(p => p.At.Year == year && p.Track.Title.Length > 0).OrderBy(p => p.At).ToArray();
        var songs = plays.GroupBy(p => p.Track.Isrc ?? p.Track.Id)
            .Select(g => new RecapSong(g.Last().Track, g.Last().Album, g.Count())).OrderByDescending(s => s.Plays).Take(top).ToArray();
        var artists = plays.Where(p => p.Track.Artists.Count > 0).GroupBy(p => p.Track.Artists[0].Id)
            .Select(g => new RecapArtist(g.Last().Track.Artists[0], g.Last().Track.Artwork, g.Count())).OrderByDescending(a => a.Plays).Take(top).ToArray();
        var albums = plays.GroupBy(p => p.Track.ReleaseId)
            .Select(g => new RecapAlbum(g.Key, g.Last().Album, g.Last().Track.Artists, g.Last().Track.Artwork, g.Count())).OrderByDescending(a => a.Plays).Take(top).ToArray();
        var month = plays.GroupBy(p => p.At.Month).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault()?.Key;
        var minutes = (int)Math.Round(plays.Sum(p => (p.Track.DurationMs ?? 0) / 60000.0));
        return new MusicRecap(year, plays.Length, minutes, plays.Select(p => p.Track.Isrc ?? p.Track.Id).Distinct().Count(),
            plays.Where(p => p.Track.Artists.Count > 0).Select(p => p.Track.Artists[0].Id).Distinct().Count(),
            month is { } m ? CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(m) : null, songs, artists, albums);
    }

    public static string Count(int n, string one) => n == 1 ? "1 " + one : n.ToString("N0", CultureInfo.InvariantCulture) + " " + one + "s";

    public string Summary => Plays == 0 ? "Nothing played yet this year. Your recap fills in as you listen."
        : $"{Count(Plays, "play")}, {Count(Minutes, "minute")}, {Count(Songs, "song")} by {Count(Artists, "artist")}."
          + (TopSongs.Count > 0 ? $" Most played: {TopSongs[0].Track.Title} ({Count(TopSongs[0].Plays, "play")})." : "")
          + (TopArtists.Count > 0 ? $" Top artist: {TopArtists[0].Artist.Name}." : "")
          + (TopMonth is { } month ? $" Busiest month: {month}." : "");
}
