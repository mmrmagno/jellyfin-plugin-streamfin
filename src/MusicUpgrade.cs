using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// The songs people keep coming back to (starred, in a playlist, or played more than once) are kept in the song cache
// as FLAC, so they play at once even while Monochrome is down: ones never kept are fetched, and ones kept as AAC from
// an outage are fetched again. It runs hourly and whenever Monochrome is healthy again, one song at a time with a
// pause after each (Monochrome refuses addresses that fetch many whole songs in a row), only while the catalog has
// budget to spare, and favourites never take more than half the cache.
public sealed class MusicUpgradeTask(MusicAudio sources, MusicCatalog catalog, MusicCache songs, MusicLibraryStore store, ILogger<MusicUpgradeTask> logger)
    : IScheduledTask
{
    private const int PerRun = 30;
    private const long Typical = 35L * 1024 * 1024;
    private static readonly TimeSpan SongLimit = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(20);

    public string Name => "Keep favourite songs ready";
    public string Key => "StreamFinMusicUpgrade";
    public string Description => "Keeps starred songs, songs in playlists and songs played more than once in the song cache as FLAC, so they play at once even while Monochrome is down. Songs that played from YouTube are fetched again as FLAC.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is not { Enabled: true, MusicEnabled: true } || !songs.Keeps) return;
        songs.Load();
        var wanted = await WantedAsync(cancellationToken);
        var half = songs.LimitBytes / 2;
        var ready = new List<string>();
        long used = 0;
        bool Ready(string id)
        {
            if (songs.Kept(id) != "flac" || used >= half) return false;
            ready.Add(id);
            used += MusicCache.Size(id) ?? Typical;
            return true;
        }
        var missing = wanted.Where(t => !Ready(t.Id)).ToList();
        songs.Favour(ready);
        var started = 0;
        for (var i = 0; i < missing.Count && started < PerRun && used + Typical <= half && !catalog.Down && catalog.Spare; i++)
        {
            progress.Report(100.0 * i / missing.Count);
            var track = missing[i];
            if (MusicCache.Fetching(track.Id)) continue;
            if (MusicCatalog.PreferExplicit) track = await catalog.ExplicitAsync(track, cancellationToken, TimeSpan.FromSeconds(4));
            if (track.Id != missing[i].Id && Ready(track.Id)) continue;
            if (!await sources.PrepareAsync(track, cancellationToken)) continue;
            started++;
            var until = DateTime.UtcNow + SongLimit;
            while (MusicCache.Fetching(track.Id) && DateTime.UtcNow < until) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (Ready(track.Id)) songs.Favour(ready);
            await Task.Delay(Pause, cancellationToken);
        }
        songs.Favour(ready);
        if (started > 0) logger.LogInformation("StreamFin music: kept {Count} favourite songs ready as FLAC", started);
        progress.Report(100);
    }

    // Starred songs first, then songs in playlists, then the most played.
    private async Task<IReadOnlyList<MusicTrack>> WantedAsync(CancellationToken ct)
    {
        List<MusicTrack> starred = [], listed = [], played = [];
        foreach (var user in MusicSaver.Users())
        {
            var library = await store.ReadAsync(user, ct);
            starred.AddRange(library.StarredTracks.Select(p => p.Track));
            listed.AddRange(library.Playlists.SelectMany(p => p.Entries).Select(e => e.Track));
            played.AddRange(library.Plays.Select(p => p.Track));
        }
        var often = played.GroupBy(t => t.Id).Where(g => g.Count() > 1).OrderByDescending(g => g.Count()).Select(g => g.First());
        return starred.Concat(listed).Concat(often).DistinctBy(t => t.Id).ToArray();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }
    ];
}
