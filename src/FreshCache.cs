using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

// Lookups that are slow to make and change slowly (which titles a site has, an episode list, a radio mix). A known
// answer comes back at once, and once it is older than its lifetime a fresh one loads behind it; for a day it still
// answers while the site is failing. Callers asking at the same time share one load, which runs on its own deadline (90 s),
// so a viewer leaving the page does not cancel it for the others. Never for signed playback addresses.
public sealed class FreshCache<T>(Func<T, bool> usable, int size = 1000, Func<DateTimeOffset>? clock = null, Action<Exception>? failed = null) : IDisposable
{
    private sealed record Entry(T Value, DateTimeOffset FreshUntil, DateTimeOffset Expires);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = size });
    private readonly ConcurrentDictionary<string, Lazy<Task<T>>> pending = new();
    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<T> GetAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, CancellationToken ct, bool spare = true)
    {
        ct.ThrowIfCancellationRequested();
        if (cache.TryGetValue<Entry>(key, out var saved))
        {
            // A refresh only uses budget the site has to spare; the old answer is fine for a while longer.
            if (saved!.FreshUntil <= Now && spare) _ = Load(key, lifetime, load, saved);
            return saved.Value;
        }
        return await Load(key, lifetime, load, null).WaitAsync(ct);
    }

    private Task<T> Load(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved)
        => pending.GetOrAdd(key, _ => new Lazy<Task<T>>(() => RunAsync(key, lifetime, load, saved))).Value;

    private async Task<T> RunAsync(string key, TimeSpan lifetime, Func<CancellationToken, Task<T>> load, Entry? saved)
    {
        await Task.Yield();
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var result = await load(limit.Token);
            if (usable(result)) Store(key, new Entry(result, Now + lifetime, DateTimeOffset.UtcNow.AddHours(24)));
            else if (saved is not null) return Retry(key, saved);
            return result;
        }
        // A failed refresh keeps the last good answer and tries again in 30 s, not on every request.
        catch (Exception ex) when (saved is not null)
        {
            failed?.Invoke(ex);
            return Retry(key, saved);
        }
        finally { pending.TryRemove(key, out _); }
    }

    private T Retry(string key, Entry saved)
    {
        Store(key, saved with { FreshUntil = Now.AddSeconds(30) });
        return saved.Value;
    }

    private void Store(string key, Entry entry) => cache.Set(key, entry, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpiration = entry.Expires });

    public void Forget(string key) => cache.Remove(key);

    // The last answer, however old, without loading anything.
    public bool TryPeek(string key, out T value)
    {
        var found = cache.TryGetValue<Entry>(key, out var saved);
        value = found ? saved!.Value : default!;
        return found;
    }

    public void Dispose() => cache.Dispose();
}
