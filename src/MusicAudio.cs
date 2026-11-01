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
    // A copy on this server's disk with no address to fetch it again from.
    public bool Local => Upstream.IsFile;
    public string ContentType => MusicAudio.ContentType(Container);
}

public sealed record YouTubeHit(string Id, string Title, string Channel, double? Duration);

// Finds where a song can play from right now, best first: this server's own copy, the catalog's FLAC, then YouTube.
// A source that is down is skipped at once, and the whole search stays well inside the minute players and proxies
// wait for an answer.
public sealed class MusicAudio(SafeHttp http, MusicCatalog catalog, MusicCache songs, YtDlp tools, MonochromeHealth health, ILogger<MusicAudio> logger)
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(40);
    // A healthy catalog answers in well under this; past it, YouTube starts looking in parallel.
    private static readonly TimeSpan Head = TimeSpan.FromSeconds(4);
    private static readonly MemoryCache Tickets = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly MemoryCache Matches = new(new MemoryCacheOptions { SizeLimit = 20000 });
    private static readonly MemoryCache Failures = new(new MemoryCacheOptions { SizeLimit = 20000 });
    // Players ask for one song several times at once; they share a single YouTube lookup.
    private static readonly ConcurrentDictionary<string, Task<AudioTicket?>> Searching = new();
    private readonly CatalogBreaker youtube = new() { Name = "YouTube", Logger = logger };

    public SourceStatus YouTubeStatus(DateTime now) => youtube.Status(now);

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
            // A lossy copy kept from an outage plays at once, and is replaced by the FLAC in the background once the
            // catalog sends it. A catalog that hangs without being marked down is asked once per song in 10 minutes,
            // so no play or seek waits on it.
            if (container != "flac" && only is null && !catalog.Down && !HasFailed(track.Id, "upgrade"))
            {
                Failures.Set(track.Id + ":upgrade", true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
                _ = Task.Run(async () =>
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try { await UpgradeAsync(track, limit.Token); }
                    catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or OperationCanceledException) { }
                }, CancellationToken.None);
            }
            return new AudioTicket(track.Id, "disk", container == "flac" && catalog.KnownTwin(track.Id) is { } twin ? MusicCatalog.StreamUri(twin) : Placeholder(track.Id), container);
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
        var useCatalog = UseCatalog(only, lossy, health.Usable, HasFailed(track.Id, "monochrome"));
        Task<AudioTicket?>? flac = useCatalog ? MonochromeAsync(track, lossy, budget.Token) : null;
        using var stopYouTube = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        Task<AudioTicket?>? backup = null;
        if (flac is not null)
        {
            await Task.WhenAny(flac, Task.Delay(Head, budget.Token).ContinueWith(_ => { }, TaskScheduler.Default));
            if (!flac.IsCompleted && lossy) backup = YouTubeAsync(track, stopYouTube.Token);
            // Whichever answers first plays, so a catalog that hangs costs nothing once YouTube has the song; the
            // FLAC still wins whenever it is there.
            if (backup is not null && await Task.WhenAny(flac, backup) == backup && !flac.IsCompleted && await backup is { } early)
                return Remember(early);
            if (await flac is { } ticket)
            {
                stopYouTube.Cancel();
                return Remember(ticket);
            }
        }
        if (lossy && (backup ??= YouTubeAsync(track, stopYouTube.Token)) is { } search && await search is { } found) return Remember(found);
        throw new ProviderException(lossy || !AllowLossy
            ? "This song cannot be played right now: the music catalog is not answering and no other source has it."
            : !YtDlp.Wanted
            ? "This song cannot be played right now: the music catalog is not answering. Turning on the YouTube fallback in the StreamFin settings lets songs play anyway."
            : YtDlp.Problem is { } problem
            ? "This song cannot be played right now: the music catalog is not answering and YouTube is not working on this server. " + problem
            : "This song cannot be played right now: the music catalog is not answering and YouTube is still being set up.");
    }

    // Whether to ask Monochrome for a song. With YouTube to fall back on, a Monochrome that is not healthy is skipped
    // at once, with no head start to wait out.
    public static bool UseCatalog(string? only, bool lossy, bool usable, bool failed) => only is null or "monochrome" && !(lossy && (!usable || failed));

    // A song kept lossy from an outage, fetched again as the catalog's FLAC when the catalog has it and answers
    // quickly. True once that download is under way; the lossy copy plays until it is done.
    public async Task<bool> UpgradeAsync(MusicTrack track, CancellationToken ct)
    {
        if (songs.Kept(track.Id) != "m4a" || MusicCache.Fetching(track.Id) || HasFailed(track.Id, "monochrome") || catalog.Down) return false;
        if (await MonochromeAsync(track, true, ct, TimeSpan.FromSeconds(20)) is not { } flac) return false;
        songs.Warm(flac);
        return MusicCache.Fetching(track.Id);
    }

    // A lossy copy kept on disk has no address of its own; this one is refused by every request policy.
    private static Uri Placeholder(string trackId) => new("file:///streamfin/" + trackId);

    private AudioTicket Remember(AudioTicket ticket)
    {
        var lifetime = ticket.Expires is { } expires ? expires - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(10);
        if (lifetime > TimeSpan.Zero)
            Tickets.Set(ticket.TrackId, ticket, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = lifetime });
        if (ticket.Source == "monochrome") health.LastTrack = ticket.Upstream;
        logger.LogInformation("StreamFin music: {Track} plays from {Source} ({Container})", ticket.TrackId, ticket.Source, ticket.Container);
        return ticket;
    }

    // With YouTube behind it, the catalog proves it can send the song before it is chosen; on its own it is
    // chosen as before, and the player waits on it as long as it takes. A song found through Deezer plays the
    // catalog's copy of the same recording, when it has one.
    private async Task<AudioTicket?> MonochromeAsync(MusicTrack track, bool fallback, CancellationToken ct, TimeSpan? limit = null)
    {
        var started = DateTime.UtcNow;
        var patience = limit ?? TimeSpan.FromSeconds(20);
        string? id;
        try { id = MusicCatalog.IsMonochrome(track.Id) ? track.Id : await catalog.TwinAsync(track, ct).WaitAsync(fallback ? patience : Timeout.InfiniteTimeSpan, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TimeoutException or OperationCanceledException) { return null; }
        if (id is null) return null;
        var upstream = MusicCatalog.StreamUri(id);
        var ticket = new AudioTicket(track.Id, "monochrome", upstream, "flac");
        if (!fallback) return ticket;
        if (!health.Breaker.Allow(DateTime.UtcNow)) return null;
        var left = patience - (DateTime.UtcNow - started);
        if (left < TimeSpan.FromSeconds(1)) return null;
        var asked = DateTime.UtcNow;
        try
        {
            using var response = await http.MediaAsync(upstream, left, ct, "bytes=0-0");
            if (response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.OK)
            {
                health.Breaker.Success(DateTime.UtcNow, DateTime.UtcNow - asked, MonochromeProbe.AudioSlow);
                return ticket;
            }
            if ((int)response.StatusCode >= 500) health.Breaker.Failure(DateTime.UtcNow, "audio HTTP " + (int)response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ProviderException or OperationCanceledException)
        {
            if (limit is null && (CatalogBreaker.IsOutage(ex, ct) || ex is OperationCanceledException)) health.Breaker.Failure(DateTime.UtcNow, "audio " + CatalogBreaker.Cause(ex));
            return null;
        }
    }

    private static bool Searchable(MusicTrack track) => track.Title.Length > 0 && track.Artists.Count > 0 && track.DurationMs is > 0;

    private async Task<AudioTicket?> YouTubeAsync(MusicTrack track, CancellationToken ct)
    {
        // The shared lookup runs on its own clock, so one player hanging up does not cancel it for the others.
        var search = Searching.GetOrAdd(track.Id, id => Task.Run(async () =>
        {
            using var own = new CancellationTokenSource(TimeSpan.FromSeconds(40) + Fetching(track));
            try { return await FindAsync(track, own.Token); }
            finally { Searching.TryRemove(id, out _); }
        }));
        try { return await search.WaitAsync(ct); }
        catch (OperationCanceledException) { return null; }
    }

    // A whole song through a VPN: about a second a minute of audio, with room for a slow day.
    private static TimeSpan Fetching(MusicTrack track) => TimeSpan.FromSeconds(Math.Clamp(30 + (track.DurationMs ?? 0) / 1000 / 4, 30, 150));

    private async Task<AudioTicket?> FindAsync(MusicTrack track, CancellationToken ct)
    {
        if (YtDlp.DenoFile is not { } deno) return null;
        try
        {
            if (!songs.Keeps && YtDlp.ProxySetting is not null) throw new ProviderException("The YouTube proxy needs the song cache, which is turned off in the StreamFin settings.");
            if (!Matches.TryGetValue<string>(track.Id, out var video))
            {
                var json = await tools.RunAsync(YtDlp.SearchArguments(deno, $"{track.Artists[0].Name} - {track.Title}"), TimeSpan.FromSeconds(20), ct);
                var best = Best(ParseSearch(json), track);
                video = best?.Id ?? "";
                if (best is null) logger.LogInformation("StreamFin music: YouTube has no copy of {Track} that matches its title, artist and length", track.Id);
                // A song YouTube does not have is not searched for again for a while.
                Matches.Set(track.Id, video, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = best is null ? TimeSpan.FromMinutes(30) : TimeSpan.FromDays(1) });
            }
            if (video is not { Length: > 0 }) return null;
            // yt-dlp keeps the song itself and it plays from disk: behind a proxy, the address it would hand over only
            // works inside its own run. Without a song cache the address is played directly, as before.
            if (songs.Keeps)
            {
                // The catalog's FLAC may have answered while YouTube was searched; then the song is not fetched twice.
                if (Known(track.Id) is { Lossless: true }) return null;
                var kept = await songs.KeepAsync(track.Id, "m4a", (part, token) => tools.RunAsync(YtDlp.DownloadArguments(deno, video, Path.GetFileName(part)), Fetching(track), token,
                    Path.GetDirectoryName(part)), ct);
                youtube.Success(DateTime.UtcNow);
                return new AudioTicket(track.Id, "youtube", Placeholder(track.Id), "m4a", track.DurationMs is > 0 ? (int)Math.Round(kept * 8.0 / track.DurationMs.Value) : 128, kept);
            }
            var output = await tools.RunAsync(YtDlp.AudioArguments(deno, video), TimeSpan.FromSeconds(25), ct);
            var url = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var upstream) || !IsAudioAddress(upstream))
                throw new ProviderException("YouTube gave an unexpected audio address.");
            youtube.Success(DateTime.UtcNow);
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
            if (ex is ToolFailedException) youtube.Failure(DateTime.UtcNow, "yt-dlp failed");
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
        var score = 0;
        // Someone else's upload of the song (a cover, a fan edit) names neither the artist nor their channel. The one
        // exception is YouTube's own album audio for releases by many artists, which sits on a shared "Release - Topic"
        // style channel: it is taken only under the song's exact title and within 2 s.
        if (artist.Length > 0 && !candidate.Contains(artist, StringComparison.Ordinal) && !channel.Contains(artist, StringComparison.Ordinal))
        {
            if (!channel.EndsWith(" topic", StringComparison.Ordinal) || candidate != title || Math.Abs(duration - track.DurationMs.Value / 1000.0) > 2) return null;
            score -= 5;
        }
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
