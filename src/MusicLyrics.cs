using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Lyrics;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// Lyrics from LRCLIB, the open database Monochrome's own player reads. Jellyfin only reads lyric files
// stored beside local media, so ChannelAccessFilter answers the lyrics route for music channel tracks.
public sealed class MusicLyrics(SafeHttp http, MusicCatalog catalog) : IDisposable
{
    public const string Host = "lrclib.net";
    private const int LineLimit = 2000;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private readonly RateWindow budget = new(10, TimeSpan.FromSeconds(10));

    public async Task<LyricDto?> ForItemAsync(Audio audio, CancellationToken ct)
    {
        if (!MusicChannel.IsAvailable || MusicChannel.ParseTrackId(audio.ExternalId ?? "") is not var (releaseId, trackId)) return null;
        try
        {
            var release = await catalog.ReleaseAsync(releaseId, ct);
            return release.Tracks.FirstOrDefault(t => t.Id == trackId) is { } track ? await GetAsync(track, release.Title, ct) : null;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException
            or TaskCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    private async Task<LyricDto?> GetAsync(MusicTrack track, string album, CancellationToken ct)
    {
        if (track.Artists.Count == 0) return null;
        if (cache.TryGetValue<LyricDto?>("lyrics/" + track.Id, out var known)) return known;
        var query = "track_name=" + Uri.EscapeDataString(track.Title) + "&artist_name=" + Uri.EscapeDataString(track.Artists[0].Name);
        LyricDto? lyrics;
        // The exact lookup needs title, artist, album and length to match; the search is the fallback for small differences.
        try
        {
            lyrics = Parse(await QueryAsync("api/get?" + query + "&album_name=" + Uri.EscapeDataString(album)
                + (track.DurationMs is > 0 ? "&duration=" + Math.Round(track.DurationMs.Value / 1000.0).ToString(CultureInfo.InvariantCulture) : ""), ct), track, album);
        }
        catch (ProviderException ex) when (ex is not RateLimitedException) { lyrics = null; }
        lyrics ??= Parse(await QueryAsync("api/search?" + query, ct), track, album);
        cache.Set("lyrics/" + track.Id, lyrics, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lyrics is null ? TimeSpan.FromHours(1) : TimeSpan.FromHours(24) });
        return lyrics;
    }

    private async Task<string> QueryAsync(string path, CancellationToken ct)
    {
        var wait = budget.Reserve(DateTime.UtcNow, TimeSpan.FromSeconds(5)) ?? throw new RateLimitedException("The lyrics service is busy. Please try again shortly.", null);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        return await http.TextAsync(new Uri("https://" + Host + "/" + path), ct);
    }

    // /api/get answers with one record and /api/search with a list. A record only counts when its
    // length is within three seconds of the track, so a live cut or a remix does not lend its words.
    public static LyricDto? Parse(string json, MusicTrack track, string album)
    {
        using var document = JsonDocument.Parse(json);
        var records = document.RootElement.ValueKind switch
        {
            JsonValueKind.Array => document.RootElement.EnumerateArray().Take(50).ToArray(),
            JsonValueKind.Object => [document.RootElement],
            _ => []
        };
        bool Fits(JsonElement r) => r.ValueKind == JsonValueKind.Object && (track.DurationMs is not > 0
            || r.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds)
            && Math.Abs(seconds * 1000 - track.DurationMs.Value) <= 3000);
        bool Instrumental(JsonElement r) => r.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;
        var best = records.Where(Fits).Where(r => !Instrumental(r))
            .OrderByDescending(r => Text(r, "syncedLyrics") is not null)
            .ThenByDescending(r => string.Equals(Text(r, "albumName"), album, StringComparison.OrdinalIgnoreCase))
            .Select(r => Lines(Text(r, "syncedLyrics"), true) is { Count: > 0 } synced ? (Lines: synced, Synced: true)
                : (Lines: Lines(Text(r, "plainLyrics"), false), Synced: false))
            .FirstOrDefault(r => r.Lines.Count > 0);
        if (best.Lines is null) return null;
        return new LyricDto
        {
            Lyrics = best.Lines,
            Metadata = new LyricMetadata
            {
                Title = track.Title, Artist = string.Join(", ", track.Artists.Select(a => a.Name)), Album = album, IsSynced = best.Synced,
                Length = track.DurationMs is > 0 ? TimeSpan.FromMilliseconds(track.DurationMs.Value).Ticks : null
            }
        };
    }

    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 200_000 } s ? s : null;

    private static readonly Regex Stamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // LRC: one or more [mm:ss.xx] stamps before the words. Tags such as [ar:...] carry no stamp and are skipped.
    private static IReadOnlyList<LyricLine> Lines(string? text, bool synced)
    {
        if (text is null) return [];
        var lines = new List<LyricLine>();
        foreach (var raw in text.Split('\n').Take(LineLimit * 2))
        {
            if (lines.Count >= LineLimit) break;
            var line = raw.Trim();
            if (!synced)
            {
                lines.Add(new LyricLine(Clean(line)));
                continue;
            }
            var at = 0;
            var starts = new List<long>();
            for (var match = Stamp.Match(line); match.Success && match.Index == at; match = match.NextMatch())
            {
                var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
                var ms = int.Parse(fraction, CultureInfo.InvariantCulture) * (int)Math.Pow(10, 3 - fraction.Length);
                if (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is var second and < 60)
                    starts.Add(TimeSpan.FromMinutes(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Ticks + TimeSpan.FromSeconds(second).Ticks + TimeSpan.FromMilliseconds(ms).Ticks);
                at = match.Index + match.Length;
            }
            var words = Clean(line[at..]);
            foreach (var start in starts.Take(20)) lines.Add(new LyricLine(words, start));
        }
        if (!synced)
        {
            // Blank lines at either end are padding; inside they separate verses and stay.
            var first = lines.FindIndex(l => l.Text.Length > 0);
            if (first < 0) return [];
            return lines.Skip(first).Take(lines.FindLastIndex(l => l.Text.Length > 0) - first + 1).ToArray();
        }
        return lines.Any(l => l.Text.Length > 0) ? lines.OrderBy(l => l.Start).Take(LineLimit).ToArray() : [];
    }

    private static string Clean(string text)
    {
        var plain = text.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim();
        return plain.Length > 500 ? plain[..500] : plain;
    }

    public void Dispose() => cache.Dispose();
}
