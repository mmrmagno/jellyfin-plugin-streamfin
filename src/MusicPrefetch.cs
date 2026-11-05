using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// While a song plays, the one after it is fetched into the song cache, so it starts at once: from Monochrome when it
// is healthy, otherwise from YouTube, which takes 15-20 s that nobody then waits for. "After it" is the next song in
// the play queue a Subsonic app saved, or else the next on the album. Only one song ahead, one at a time, and only
// while the catalog has budget to spare: Monochrome refuses addresses that fetch many whole songs in a row.
public sealed class MusicPrefetch(MusicAudio audio, MusicCatalog catalog, MusicCache songs, MusicLibraryStore store, ILogger<MusicPrefetch> logger)
{
    private static readonly SemaphoreSlim Running = new(1, 1);
    private static readonly MemoryCache Done = new(new MemoryCacheOptions { SizeLimit = 5000 });

    public static bool Enabled => Plugin.Instance?.Configuration.MusicPrefetch != false;

    public void After(Guid user, MusicTrack track)
    {
        var key = user.ToString("N") + track.Id;
        if (!Enabled || !songs.Keeps || Done.TryGetValue(key, out _)) return;
        _ = Task.Run(async () =>
        {
            if (!await Running.WaitAsync(0)) return;
            Done.Set(key, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                if (await NextAsync(user, track, limit.Token) is not { } next || !catalog.Spare) return;
                var recording = MusicCatalog.PreferExplicit ? await catalog.ExplicitAsync(next, limit.Token, TimeSpan.FromSeconds(4)) : next;
                if (songs.Kept(recording.Id) is not null || MusicCache.Fetching(recording.Id)) return;
                logger.LogInformation("StreamFin music: getting {Track} ready to play next", recording.Id);
                var ticket = await audio.ResolveAsync(recording, limit.Token);
                if (ticket.Source == "monochrome") songs.Warm(ticket);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException or OperationCanceledException) { }
            finally { Running.Release(); }
        }, CancellationToken.None);
    }

    private async Task<MusicTrack?> NextAsync(Guid user, MusicTrack track, CancellationToken ct)
    {
        if (user != Guid.Empty && (await store.ReadAsync(user, ct)).Queue is { } queue && Next(queue.Entries.Select(e => e.Track).ToArray(), track.Id) is { } queued)
            return queued;
        // Only an album known without asking the catalog: a slow catalog must not hold up the song that plays now.
        if (!catalog.HasRelease(track.ReleaseId)) return null;
        return Next((await catalog.ReleaseAsync(track.ReleaseId, ct)).Tracks, track.Id);
    }

    public static MusicTrack? Next(IReadOnlyList<MusicTrack> list, string trackId)
    {
        for (var i = 0; i < list.Count - 1; i++)
            if (list[i].Id == trackId) return list[i + 1];
        return null;
    }
}
