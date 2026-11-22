using StreamFin;
using static Harness;

// Small shared pieces: requests merged while in flight, and per-user music history.
static class SharedChecks
{
    public static async Task RunAsync()
    {
        var flight = new InFlight<int>(TimeSpan.FromSeconds(5));
        var loads = 0;
        var gate = new TaskCompletionSource();
        async Task<int> Load(CancellationToken token) { Interlocked.Increment(ref loads); await gate.Task.WaitAsync(token); return 42; }
        using var leaving = new CancellationTokenSource();
        var first = flight.GetAsync("x", Load, leaving.Token);
        var second = flight.GetAsync("x", Load, default);
        leaving.Cancel();
        gate.SetResult();
        var firstCancelled = false;
        try { await first; } catch (OperationCanceledException) { firstCancelled = true; }
        Check(await second == 42 && loads == 1 && firstCancelled, "callers asking together share one load, and one leaving does not cancel it for the other");
        Check(await flight.GetAsync("x", _ => Task.FromResult(7), default) == 7 && flight.Count == 0, "a finished load is not kept, so the next ask loads again");

        var root = Path.Combine(Path.GetTempPath(), "streamfin-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new MusicHistory(root);
            var (one, two) = (Guid.NewGuid(), Guid.NewGuid());
            MusicTrack Song(string id) => new(id, "Song " + id, [new MusicArtistRef("a" + id, "Artist " + id)], "r" + id, null, 180_000, null, false, null, null);
            var at = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
            await history.AppendAsync(one, [new(Song("1"), "Album", at), new(Song("1"), "Album", at.AddHours(1))], default);
            await history.AppendAsync(two, [new(Song("2"), "Album", at)], default);
            var recapOne = MusicRecap.Build(2026, await history.ReadAsync(one, 2026, default));
            var recapTwo = MusicRecap.Build(2026, await history.ReadAsync(two, 2026, default));
            Check(recapOne is { Plays: 2, TopSongs: [{ Track.Id: "1" }] } && recapTwo is { Plays: 1, TopSongs: [{ Track.Id: "2" }] }
                && history.Years(Guid.NewGuid()).Count == 0, "two people's plays and recaps never mix");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
