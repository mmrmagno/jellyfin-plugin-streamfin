using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// One playable copy of a song: where its bytes come from and what they are. A song keeps the source it started
// with for the whole play, because two sources' files never line up byte for byte.
public sealed record AudioTicket(string TrackId, string Source, Uri Upstream, string Container, int? Kbps = null, long? Size = null, DateTimeOffset? Expires = null)
{
    public bool Lossless => Container == "flac";
    public string ContentType => MusicAudio.ContentType(Container);
}

public sealed record YouTubeHit(string Id, string Title, string Channel, double? Duration);

// Finds where a song can play from right now, best first: this server's own copy, the catalog's FLAC, then YouTube.
// A source that is down is skipped at once, and the whole search stays well inside the minute players and proxies
// wait for an answer.
public sealed class MusicAudio(SafeHttp http, MusicCatalog catalog, MusicCache songs, YtDlp tools, ILogger<MusicAudio> logger)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);
    // A healthy catalog answers in well under this; past it, YouTube starts looking in parallel.
    private static readonly TimeSpan Head = TimeSpan.FromSeconds(4);
    private static readonly MemoryCache Tickets = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly MemoryCache Matches = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly MemoryCache Failures = new(new MemoryCacheOptions { SizeLimit = 20000 });
    // Players ask for one song several times at once; they share a single YouTube lookup.
    private static readonly ConcurrentDictionary<string, Task<AudioTicket?>> Searching = new();
    private readonly CatalogBreaker monochrome = new();
    private readonly CatalogBreaker youtube = new();

    public static bool AllowLossy => Plugin.Instance?.Configuration.MusicAllowLossy != false;
    public static string ContentType(string container) => container == "m4a" ? "audio/mp4" : "audio/flac";

    // The last copy found for a song, so its details can say what a player will get before it asks.
    public static AudioTicket? Known(string trackId) => Tickets.TryGetValue<AudioTicket>(trackId, out var ticket) ? ticket : null;

    // A copy that broke while playing: the next play of this song looks elsewhere.
    public static void Failed(AudioTicket ticket)
    {
        Tickets.Remove(ticket.TrackId);
        if (ticket.Source == "youtube") Matches.Remove(ticket.TrackId);
        Failures.Set(ticket.TrackId + ":" + ticket.Source, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }

    public static void Forget(string trackId) => Tickets.Remove(trackId);

    private static bool HasFailed(string trackId, string source) => Failures.TryGetValue(trackId + ":" + source, out _);

    // With only, a song already playing gets another copy from the same source (a link that expired), never a different one.
    public async Task<AudioTicket> ResolveAsync(MusicTrack track, CancellationToken ct, string? only = null)
    {
        if (only is null or "disk" && songs.Kept(track.Id) is { } container && (container == "flac" || AllowLossy))
        {
            // A lossy copy kept from an outage gives way to the FLAC as soon as the catalog answers quickly again.
            if (container != "flac" && only is null && !catalog.Down
                && await MonochromeAsync(track, true, ct, TimeSpan.FromSeconds(5)) is { } better) return Remember(better);
            return new AudioTicket(track.Id, "disk", container == "flac" ? MusicCatalog.StreamUri(track.Id) : Placeholder(track.Id), container);
        }
        // A copy on disk that was since evicted may come from any source, as long as the format stays the same.
        if (only == "disk") only = null;
        if (Known(track.Id) is { } known && (only is null || known.Source == only) && known.Source != "disk"
            && (known.Expires is null || known.Expires > DateTimeOffset.UtcNow.AddMinutes(10)))
            return known;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        var lossy = AllowLossy && YtDlp.Wanted && Searchable(track) && !HasFailed(track.Id, "youtube") && only is null or "youtube";
        if (lossy && !YtDlp.Ready) tools.Kick();
        lossy = lossy && YtDlp.Ready && youtube.Allow(DateTime.UtcNow);
        var useCatalog = only is null or "monochrome" && !(lossy && (catalog.Down || HasFailed(track.Id, "monochrome")));
        Task<AudioTicket?>? flac = useCatalog ? MonochromeAsync(track, lossy, budget.Token) : null;
        using var stopYouTube = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        Task<AudioTicket?>? backup = null;
        if (flac is not null)
        {
            await Task.WhenAny(flac, Task.Delay(Head, budget.Token).ContinueWith(_ => { }, TaskScheduler.Default));
            if (!flac.IsCompleted && lossy) backup = YouTubeAsync(track, stopYouTube.Token);
            if (await flac is { } ticket)
            {
                stopYouTube.Cancel();
                return Remember(ticket);
            }
        }
        if (lossy && (backup ??= YouTubeAsync(track, stopYouTube.Token)) is { } search && await search is { } found) return Remember(found);
        throw new ProviderException(lossy || !AllowLossy || !YtDlp.Wanted
            ? "This song cannot be played right now: the music catalog is not answering and no other source has it."
            : "This song cannot be played right now: the music catalog is not answering. Turning on the YouTube fallback in the StreamFin settings lets songs play anyway.");
    }

    // A lossy copy kept on disk has no address of its own; this one is refused by every request policy.
    private static Uri Placeholder(string trackId) => new("file:///streamfin/" + trackId);

    private AudioTicket Remember(AudioTicket ticket)
    {
        var lifetime = ticket.Expires is { } expires ? expires - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(10);
        if (lifetime > TimeSpan.Zero)
            Tickets.Set(ticket.TrackId, ticket, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        logger.LogInformation("StreamFin music: {Track} plays from {Source} ({Container})", ticket.TrackId, ticket.Source, ticket.Container);
        return ticket;
    }

    // With YouTube behind it, the catalog proves it can send the song before it is chosen; on its own it is
    // chosen as before, and the player waits on it as long as it takes.
    private async Task<AudioTicket?> MonochromeAsync(MusicTrack track, bool fallback, CancellationToken ct, TimeSpan? limit = null)
    {
        var upstream = MusicCatalog.StreamUri(track.Id);
        var ticket = new AudioTicket(track.Id, "monochrome", upstream, "flac");
        if (!fallback) return ticket;
        if (!monochrome.Allow(DateTime.UtcNow)) return null;
        try
        {
            using var response = await http.MediaAsync(upstream, limit ?? TimeSpan.FromSeconds(20), ct, "bytes=0-0");
            if (response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.OK)
            {
                monochrome.Success();
                return ticket;
            }
            if ((int)response.StatusCode >= 500) monochrome.Failure(DateTime.UtcNow);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ProviderException or OperationCanceledException)
        {
            if (limit is null && (CatalogBreaker.IsOutage(ex, ct) || ex is OperationCanceledException)) monochrome.Failure(DateTime.UtcNow);
            return null;
        }
    }

    private static bool Searchable(MusicTrack track) => track.Title.Length > 0 && track.Artists.Count > 0 && track.DurationMs is > 0;

    private async Task<AudioTicket?> YouTubeAsync(MusicTrack track, CancellationToken ct)
    {
        // The shared lookup runs on its own clock, so one player hanging up does not cancel it for the others.
        var search = Searching.GetOrAdd(track.Id, id => Task.Run(async () =>
        {
            using var own = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            try { return await FindAsync(track, own.Token); }
            finally { Searching.TryRemove(id, out _); }
        }));
        try { return await search.WaitAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    private async Task<AudioTicket?> FindAsync(MusicTrack track, CancellationToken ct)
    {
        if (YtDlp.DenoFile is not { } deno) return null;
        try
        {
            if (!Matches.TryGetValue<string>(track.Id, out var video))
            {
                var json = await tools.RunAsync(YtDlp.SearchArguments(deno, $"{track.Artists[0].Name} - {track.Title}"), TimeSpan.FromSeconds(20), ct);
                var best = Best(ParseSearch(json), track);
                video = best?.Id ?? "";
                // A song YouTube does not have is not searched for again for a while.
                Matches.Set(track.Id, video, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = best is null ? TimeSpan.FromMinutes(30) : TimeSpan.FromDays(1) });
            }
            if (video is not { Length: > 0 }) return null;
            var output = await tools.RunAsync(YtDlp.AudioArguments(deno, video), TimeSpan.FromSeconds(25), ct);
            var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var upstream) || !IsAudioAddress(upstream))
                throw new ProviderException("YouTube gave an unexpected audio address.");
            youtube.Success();
            var query = Query(upstream);
            long? size = long.TryParse(query.GetValueOrDefault("clen"), NumberStyles.None, CultureInfo.InvariantCulture, out var clen) && clen is > 0 and <= MusicCache.SongLimit ? clen : null;
            double? seconds = double.TryParse(query.GetValueOrDefault("dur"), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur) && dur > 0 ? dur : null;
            DateTimeOffset? expires = long.TryParse(query.GetValueOrDefault("expire"), NumberStyles.None, CultureInfo.InvariantCulture, out var expire)
                ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(expire, DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds())) : DateTimeOffset.UtcNow.AddHours(1);
            return new AudioTicket(track.Id, "youtube", upstream, "m4a", size is long s && seconds is double d ? (int)Math.Round(s * 8 / d / 1000) : 128, size, expires);
        }
        // Whatever goes wrong, the song is simply not on YouTube this time. Only yt-dlp itself failing counts
        // against YouTube: being busy or slow says nothing about whether it works.
        catch (Exception ex)
        {
            if (ex is ToolFailedException) youtube.Failure(DateTime.UtcNow);
            logger.LogInformation("StreamFin music: YouTube has no copy of {Track} ({Reason})", track.Id, ex is ProviderException ? ex.Message : ex.GetType().Name);
            return null;
        }
    }

    public static bool IsAudioAddress(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.IdnHost.EndsWith(".googlevideo.com", StringComparison.Ordinal) && SafeHttp.Allowed(uri, true);

    private static Dictionary<string, string> Query(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            if (pair.Split('=', 2) is [var key, var value]) values.TryAdd(key, Uri.UnescapeDataString(value));
        return values;
    }

    public static IReadOnlyList<YouTubeHit> ParseSearch(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return [];
        var hits = new List<YouTubeHit>();
        foreach (var entry in entries.EnumerateArray().Take(20))
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            string? Text(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            double? duration = entry.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var seconds) ? seconds : null;
            if (Text("id") is { Length: 11 } id && Regex.IsMatch(id, "^[A-Za-z0-9_-]{11}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                hits.Add(new YouTubeHit(id, Text("title") ?? "", Text("channel") ?? Text("uploader") ?? "", duration));
        }
        return hits;
    }

    // Versions that are not the recording unless the song itself says so.
    private static readonly string[] Other = ["live", "remix", "cover", "sped up", "slowed", "nightcore", "karaoke", "instrumental", "acoustic", "8d", "reverb", "mashup", "bass boosted", "reaction", "tutorial", "lesson", "shorts"];

    public static int? Score(YouTubeHit hit, MusicTrack track)
    {
        if (track.DurationMs is not > 0 || hit.Duration is not double duration || Math.Abs(duration - track.DurationMs.Value / 1000.0) > 3) return null;
        var title = Normalize(track.Title);
        var candidate = Normalize(hit.Title);
        var main = Normalize(Regex.Replace(track.Title, @"\s*[\(\[].*?[\)\]]", "", RegexOptions.None, TimeSpan.FromSeconds(1)));
        if (main.Length == 0 || !candidate.Contains(main, StringComparison.Ordinal)) return null;
        var artist = Normalize(track.Artists.FirstOrDefault()?.Name ?? "");
        var channel = Normalize(hit.Channel);
        // Someone else's upload of the song (a cover, a fan edit) names neither the artist nor their channel.
        if (artist.Length > 0 && !candidate.Contains(artist, StringComparison.Ordinal) && !channel.Contains(artist, StringComparison.Ordinal)) return null;
        var score = 0;
        // YouTube's automatic "Artist - Topic" uploads are the album audio itself.
        if (channel == artist + " topic") score += 50;
        else if (artist.Length > 0 && (channel == artist || channel.Replace(" ", "") == artist.Replace(" ", "") + "vevo")) score += 25;
        if (candidate.Contains("official audio", StringComparison.Ordinal)) score += 10;
        if (candidate.Contains("official video", StringComparison.Ordinal) || candidate.Contains("music video", StringComparison.Ordinal)) score -= 5;
        foreach (var word in Other)
            if (Has(candidate, word) && !Has(title, word)) score -= 40;
        score -= (int)Math.Round(Math.Abs(duration - track.DurationMs.Value / 1000.0) * 2);
        return score;
    }

    public static YouTubeHit? Best(IReadOnlyList<YouTubeHit> hits, MusicTrack track)
        => hits.Select(h => (Hit: h, Score: Score(h, track))).Where(p => p.Score is >= -10).OrderByDescending(p => p.Score).Select(p => p.Hit).FirstOrDefault();

    private static bool Has(string text, string word) => (" " + text + " ").Contains(" " + word + " ", StringComparison.Ordinal);

    public static string Normalize(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : c == '&' ? 'n' : ' ');
        }
        return Regex.Replace(builder.ToString().Replace(" n ", " and "), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
    }
}
