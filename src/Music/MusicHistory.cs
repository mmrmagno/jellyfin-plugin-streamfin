using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StreamFin;

// Every play, kept per user and year (one JSON line each), so a yearly recap can count them. Recently played keeps
// only the last 500. Starts counting the day this file first appears; earlier plays are not known.
public sealed class MusicHistory(string? root = null)
{
    public const int YearLimit = 100_000;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();
    private string? Root => root ?? (Plugin.Instance is { } plugin ? Path.Combine(plugin.DataFolderPath, "music", "history") : null);
    private string? Folder(Guid user) => Root is { } path ? Path.Combine(path, user.ToString("N")) : null;
    private string? File(Guid user, int year) => Folder(user) is { } folder ? Path.Combine(folder, year.ToString(CultureInfo.InvariantCulture) + ".jsonl") : null;

    public async Task AppendAsync(Guid user, IEnumerable<MusicPlay> plays, CancellationToken ct)
    {
        foreach (var year in plays.Where(p => p.Track.Title.Length > 0).GroupBy(p => p.At.Year))
        {
            if (File(user, year.Key) is not { } path) return;
            var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (System.IO.File.Exists(path) && new FileInfo(path).Length > YearLimit * 2000L) continue;
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
        // A damaged line (a crash mid-write) is skipped; the rest of the year still counts.
        foreach (var line in await System.IO.File.ReadAllLinesAsync(path, ct))
        {
            if (plays.Count >= YearLimit) break;
            try { if (JsonSerializer.Deserialize<MusicPlay>(line) is { Track.Title.Length: > 0 } play) plays.Add(play); }
            catch (JsonException) { }
        }
        return plays;
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
