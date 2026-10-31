using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Songs that played from YouTube during an outage stay on disk as AAC. Once the catalog answers quickly again, the
// ones people keep coming back to (starred, in a playlist, or played more than once) are fetched again as FLAC: one
// at a time, a few per run, and only while the catalog has budget to spare. Any other song changes over the next
// time it plays.
public sealed class MusicUpgradeTask(MusicAudio sources, MusicCatalog catalog, MusicCache songs, MusicLibraryStore store, ILogger<MusicUpgradeTask> logger)
    : IScheduledTask
{
    private const int PerRun = 20;
    private static readonly TimeSpan SongLimit = TimeSpan.FromMinutes(15);

    public string Name => "Upgrade kept songs to FLAC";
    public string Key => "StreamFinMusicUpgrade";
    public string Description => "Fetches the lossless copy of songs that played from YouTube while the music catalog was down: starred songs, songs in playlists and songs played more than once.";
    public string Category => "StreamFin";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration is not { Enabled: true, MusicEnabled: true }) return;
        songs.Load();
        var wanted = await WantedAsync(cancellationToken);
        var started = 0;
        for (var i = 0; i < wanted.Count && started < PerRun && !catalog.Down && catalog.Spare; i++)
        {
            progress.Report(100.0 * i / wanted.Count);
            if (!await sources.UpgradeAsync(wanted[i], cancellationToken)) continue;
            started++;
            var until = DateTime.UtcNow + SongLimit;
            while (MusicCache.Fetching(wanted[i].Id) && DateTime.UtcNow < until) await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        if (started > 0) logger.LogInformation("StreamFin music: fetched {Count} kept songs again as FLAC", started);
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
        return starred.Concat(listed).Concat(often).DistinctBy(t => t.Id).Where(t => songs.Kept(t.Id) == "m4a" && !MusicCache.Fetching(t.Id)).ToArray();
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }
    ];
}
