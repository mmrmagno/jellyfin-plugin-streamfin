using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

public sealed record SimilarArtist(string Name, double Score);

// Radio from an artist: listeners' habits say which artists go together (ListenBrainz, or Last.fm with a key), and
// the catalog supplies those artists' top tracks. ListenBrainz knows artists by MusicBrainz ID; the exact one comes
// from the ISRC of a top track, with a name search as the fallback. Every answer is kept for days.
public sealed class MusicRadio(SafeHttp http, MusicCatalog catalog) : IDisposable
{
    public const string MusicBrainzHost = "musicbrainz.org";
    public const string ListenBrainzHost = "labs.api.listenbrainz.org";
    public const string LastFmHost = "ws.audioscrobbler.com";
    // MusicBrainz asks for at most one request a second and a User-Agent that says whom to contact.
    public const string ContactAgent = "StreamFin/0.7 ( https://github.com/mmrmagno/jellyfin-plugin-streamfin )";
    private const string Algorithm = "session_based_days_7500_session_300_contribution_5_threshold_10_limit_100_filter_True_skip_30";
    private const int ArtistLimit = 6;
    // Building a radio may wait this long for the catalog budget to have room, and no longer.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan Days = TimeSpan.FromDays(3);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 5000 });
    private readonly SemaphoreSlim brainz = new(1, 1);
    private DateTime lastBrainz;

    private static string? LastFmKey => Plugin.Instance?.Configuration.LastFmApiKey is { Length: > 0 } key
        && Regex.IsMatch(key.Trim(), @"\A[0-9a-fA-F]{32}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? key.Trim() : null;

    // Names compared loosely: "AC/DC" and "ACDC", "Simon & Garfunkel" and "Simon and Garfunkel". Empty never matches.
    public static string NameKey(string name) => Regex.Replace(name.ToLowerInvariant().Replace("&", "and", StringComparison.Ordinal), @"[^\p{L}\p{N}]+", "",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool Same(string a, string b) => NameKey(a) is { Length: > 0 } key && key == NameKey(b);
    private static bool IsMbid(string? id) => id is not null && Regex.IsMatch(id, @"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z", RegexOptions.None, TimeSpan.FromSeconds(1));

    // A mix: some of the artist's own top tracks, then the similar artists' in turn. Empty when nothing is known.
    public async Task<IReadOnlyList<MusicTrack>> RadioAsync(string artistId, int count, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Patience;
        var artist = await catalog.ArtistAsync(artistId, ct);
        var similar = await SimilarArtistsAsync(artist, ct, deadline);
        if (similar.Count == 0) return [];
        var lists = new List<Queue<MusicTrack>> { new(Shuffle(artist.TopTracks, artist.Id).Take(Math.Max(3, count / (similar.Count + 1)))) };
        foreach (var other in similar)
        {
            if (!catalog.HasArtist(other.Id) && !await RoomAsync(deadline, ct)) break;
            // One similar artist the catalog cannot load leaves a shorter mix, not none.
            try { lists.Add(new Queue<MusicTrack>(Shuffle((await catalog.ArtistAsync(other.Id, ct)).TopTracks, artist.Id).Take(5))); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException
                or OperationCanceledException && !ct.IsCancellationRequested) { if (catalog.Down) break; }
        }
        var mix = new List<MusicTrack>();
        while (mix.Count < count * 2 && lists.Any(l => l.Count > 0))
            foreach (var list in lists.Where(l => l.Count > 0)) mix.Add(list.Dequeue());
        return (await catalog.ExplicitTracksAsync(mix, ct)).Take(count).ToArray();
    }

    // The catalog budget is shared with people browsing, so a radio only uses it while half is free, waiting a little if not.
    private async Task<bool> RoomAsync(DateTime deadline, CancellationToken ct)
    {
        if (DateTime.UtcNow > deadline || catalog.Down) return false;
        while (!catalog.Spare)
        {
            if (DateTime.UtcNow + TimeSpan.FromMilliseconds(500) > deadline) return false;
            await Task.Delay(500, ct);
        }
        return true;
    }

    // The same mix all day for an artist: Jellyfin keeps the songs of a channel listing only while the listing has them,
    // so a new shuffle on every request would pull songs out from under a queue someone is playing.
    private static IEnumerable<MusicTrack> Shuffle(IEnumerable<MusicTrack> tracks, string seed)
    {
        var random = new Random((int)(long.Parse(seed, CultureInfo.InvariantCulture) % int.MaxValue) ^ DateOnly.FromDateTime(DateTime.UtcNow).DayNumber);
        return tracks.Select(t => (Key: random.Next(), Track: t)).ToArray().OrderBy(p => p.Key).Select(p => p.Track);
    }

    // Similar artists that the catalog has, best match first.
    public async Task<IReadOnlyList<MusicArtistRef>> SimilarArtistsAsync(MusicArtistPage artist, CancellationToken ct, DateTime? deadline = null)
    {
        if (cache.TryGetValue<IReadOnlyList<MusicArtistRef>>("similar/" + artist.Id, out var known)) return known!;
        var until = deadline ?? DateTime.UtcNow + Patience;
        var found = new List<MusicArtistRef>();
        var names = await SimilarNamesAsync(artist, until, ct);
        if (names is null) return [];
        var complete = true;
        foreach (var name in names.Where(n => NameKey(n.Name) != NameKey(artist.Name)).Take(ArtistLimit * 3))
        {
            if (found.Count >= ArtistLimit) break;
            var (match, answered) = await CatalogArtistAsync(name.Name, until, ct);
            if (!answered) { complete = false; break; }
            if (match is not null && match.Id != artist.Id && found.All(f => f.Id != match.Id)) found.Add(match);
        }
        // Cut short by the catalog budget, it is not the final answer; the next request carries on from the cache.
        Keep("similar/" + artist.Id, (IReadOnlyList<MusicArtistRef>)found, complete ? Days : TimeSpan.FromMinutes(2));
        return found;
    }

    private async Task<(MusicArtistRef? Match, bool Answered)> CatalogArtistAsync(string name, DateTime deadline, CancellationToken ct)
    {
        var key = "catalog/" + NameKey(name);
        if (key == "catalog/") return (null, true);
        if (cache.TryGetValue<MusicArtistRef?>(key, out var known)) return (known, true);
        if (!await RoomAsync(deadline, ct)) return (null, false);
        MusicArtistRef? match;
        try
        {
            match = (await catalog.SearchAsync(name, ct).WaitAsync(deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero, ct))
                .Artists.FirstOrDefault(a => NameKey(a.Name) == NameKey(name)) is { } page ? new MusicArtistRef(page.Id, page.Name) : null;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException or TimeoutException
            or OperationCanceledException && !ct.IsCancellationRequested) { return (null, false); }
        Keep(key, match, Days);
        return (match, true);
    }

    // Null when no source could be asked or answered in time. Only a real answer, even an empty one, is cached:
    // a failure kept as "no similar artists" would empty the radio for days.
    private async Task<IReadOnlyList<SimilarArtist>?> SimilarNamesAsync(MusicArtistPage artist, DateTime deadline, CancellationToken ct)
    {
        IReadOnlyList<SimilarArtist> names = [];
        var failed = false;
        try
        {
            if (await MbidAsync(artist, ct, deadline) is { } mbid)
            {
                using var timeout = Until(deadline, ct);
                names = await CachedAsync("listenbrainz/" + mbid, async () => ParseSimilar(await http.TextAsync(new Uri(
                    $"https://{ListenBrainzHost}/similar-artists/json?artist_mbids={mbid}&algorithm={Algorithm}"), timeout.Token), mbid), ct);
            }
        }
        catch (Exception ex) when (ex is TimeoutException || Failed(ex, ct)) { failed = true; }
        if (names.Count > 0) return names;
        if (LastFmKey is { } key)
            try
            {
                using var timeout = Until(deadline, ct);
                var other = await CachedAsync("lastfm/" + NameKey(artist.Name), async () => ParseLastFm(await http.TextAsync(new Uri(
                    $"https://{LastFmHost}/2.0/?method=artist.getsimilar&format=json&limit=30&autocorrect=1&artist={Uri.EscapeDataString(artist.Name)}&api_key={key}"), timeout.Token)), ct);
                if (other.Count > 0 || !failed) return other;
            }
            catch (Exception ex) when (Failed(ex, ct)) { failed = true; }
        return failed ? null : names;
    }

    // At least a few seconds, so a request that starts just before the deadline is not cut off at once.
    private static CancellationTokenSource Until(DateTime deadline, CancellationToken ct)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        source.CancelAfter(Remaining(deadline) + TimeSpan.FromSeconds(3));
        return source;
    }

    private static bool Failed(Exception ex, CancellationToken ct) => ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException
        || ex is TaskCanceledException && !ct.IsCancellationRequested;

    public async Task<string?> MbidAsync(MusicArtistPage artist, CancellationToken ct, DateTime? deadline = null)
    {
        var until = deadline ?? DateTime.UtcNow + TimeSpan.FromMinutes(1);
        var key = "mbid/" + artist.Id;
        if (cache.TryGetValue<string?>(key, out var known)) return known;
        string? mbid = null;
        foreach (var isrc in artist.TopTracks.Where(t => t.Artists.FirstOrDefault()?.Id == artist.Id).Select(t => t.Isrc).OfType<string>().Distinct().Take(2))
            if ((mbid = ParseIsrc(await BrainzAsync($"isrc/{isrc}?inc=artist-credits&fmt=json", until, ct), artist.Name)) is not null) break;
        mbid ??= ParseArtistSearch(await BrainzAsync("artist?fmt=json&limit=5&query=" + Uri.EscapeDataString($"artist:\"{artist.Name.Replace("\"", "", StringComparison.Ordinal)}\""), until, ct), artist.Name);
        Keep(key, mbid, mbid is null ? TimeSpan.FromHours(6) : TimeSpan.FromDays(30));
        return mbid;
    }

    // One request a second for the whole server. MusicBrainz answers 503 when busy; a short wait clears it. A caller
    // that cannot wait past its deadline gets a TimeoutException instead of queueing behind everyone else.
    private async Task<string> BrainzAsync(string path, DateTime deadline, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (!await brainz.WaitAsync(Remaining(deadline), ct)) throw new TimeoutException();
            try
            {
                var wait = lastBrainz + TimeSpan.FromSeconds(1.1) - DateTime.UtcNow;
                if (DateTime.UtcNow + wait > deadline) throw new TimeoutException();
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                lastBrainz = DateTime.UtcNow;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await http.OpenAsync(new Uri($"https://{MusicBrainzHost}/ws/2/" + path), false, timeout.Token);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 3) { lastBrainz = DateTime.UtcNow.AddSeconds(attempt); continue; }
                if (response.StatusCode == HttpStatusCode.NotFound) return "{}";
                if (!response.IsSuccessStatusCode) throw new ProviderException($"MusicBrainz returned HTTP {(int)response.StatusCode}.");
                if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024) throw new ProviderException("MusicBrainz response is too large.");
                return await response.Content.ReadAsStringAsync(timeout.Token);
            }
            finally { brainz.Release(); }
        }
    }

    private static TimeSpan Remaining(DateTime deadline) => deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> load, CancellationToken ct)
    {
        if (cache.TryGetValue<T>(key, out var known)) return known!;
        ct.ThrowIfCancellationRequested();
        var value = await load();
        Keep(key, value, Days);
        return value;
    }

    private void Keep<T>(string key, T value, TimeSpan lifetime) => cache.Set(key, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });

    private static string? Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
        && v.GetString()?.Replace("<", "", StringComparison.Ordinal).Replace(">", "", StringComparison.Ordinal).Trim() is { Length: > 0 and <= 300 } s ? s : null;
    private static IEnumerable<JsonElement> Array(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
        && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Take(200) : [];

    // The credited artist on the ISRC's recordings whose name is the catalog artist's.
    public static string? ParseIsrc(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return Array(document.RootElement, "recordings").SelectMany(r => Array(r, "artist-credit"))
            .Select(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("artist", out var a) ? a : default)
            .Where(a => Text(a, "name") is { } n && Same(n, name) && IsMbid(Text(a, "id")))
            .GroupBy(a => Text(a, "id")!).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    public static string? ParseArtistSearch(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return Array(document.RootElement, "artists").Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("score", out var s)
                && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var score) && score >= 90 && Text(a, "name") is { } n && Same(n, name))
            .Select(a => Text(a, "id")).FirstOrDefault(IsMbid);
    }

    public static IReadOnlyList<SimilarArtist> ParseSimilar(string json, string? reference = null)
    {
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().Take(200) : [])
            .Where(a => IsMbid(Text(a, "artist_mbid")) && Text(a, "artist_mbid") != reference)
            .Select(a => Text(a, "name") is { } n && a.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var score)
                && double.IsFinite(score) && score >= 0 ? new SimilarArtist(n, score) : null)
            .OfType<SimilarArtist>().DistinctBy(a => NameKey(a.Name)).OrderByDescending(a => a.Score).Take(50).ToArray();
    }

    public static IReadOnlyList<SimilarArtist> ParseLastFm(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("similarartists", out var similar) ? similar : default;
        return Array(list, "artist").Select(a => Text(a, "name") is { } n && double.TryParse(Text(a, "match"), NumberStyles.Float, CultureInfo.InvariantCulture, out var match)
                && double.IsFinite(match) && match is >= 0 and <= 1 ? new SimilarArtist(n, match) : null)
            .OfType<SimilarArtist>().DistinctBy(a => NameKey(a.Name)).OrderByDescending(a => a.Score).Take(50).ToArray();
    }

    public void Dispose()
    {
        cache.Dispose();
        brainz.Dispose();
    }
}
