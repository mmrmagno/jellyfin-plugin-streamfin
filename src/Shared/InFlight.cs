using System.Collections.Concurrent;

namespace StreamFin;

// Callers asking for the same key at the same time share one load. The load runs on its own deadline, so one caller
// leaving does not cancel it for the others; each caller still stops waiting when its own request ends.
public sealed class InFlight<T>(TimeSpan deadline)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> pending = new();

    public Task<T> GetAsync(string key, Func<CancellationToken, Task<T>> load, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return pending.GetOrAdd(key, _ => new Lazy<Task<T>>(() => RunAsync(key, load))).Value.WaitAsync(ct);
    }

    public int Count => pending.Count;

    private async Task<T> RunAsync(string key, Func<CancellationToken, Task<T>> load)
    {
        await Task.Yield();
        try
        {
            using var limit = new CancellationTokenSource(deadline);
            return await load(limit.Token);
        }
        finally { pending.TryRemove(key, out _); }
    }
}
