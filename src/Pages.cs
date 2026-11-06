namespace StreamFin;

// Catalogs hand out a page at a time; a folder shows several, fetched together so it opens as fast as one. Titles
// that moved between pages while they loaded are listed once.
public static class Pages
{
    public sealed record Loaded<T>(List<T> Items, int? Next);

    public static async Task<Loaded<T>> LoadAsync<TPage, T, TKey>(int first, int count, int last, Func<int, Task<TPage>> load,
        Func<TPage, IEnumerable<T>> items, Func<TPage, bool> hasNext, Func<T, TKey> key)
    {
        var numbers = Enumerable.Range(first, Math.Max(0, Math.Min(count, last - first + 1))).ToArray();
        if (numbers.Length == 0) return new([], null);
        async Task<TPage> Load(int number) => await load(number);
        var tasks = numbers.Select(Load).ToArray();
        // The first page decides: when it fails, the folder fails as before. A later one that fails ends the folder early.
        var head = await tasks[0];
        var found = new List<T>(items(head));
        var more = hasNext(head);
        for (var i = 1; i < tasks.Length && more; i++)
        {
            try
            {
                var page = await tasks[i];
                found.AddRange(items(page));
                more = hasNext(page);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                return new(found.DistinctBy(key).ToList(), numbers[i]);
            }
        }
        var next = numbers[^1] + 1;
        return new(found.DistinctBy(key).ToList(), more && next <= last ? next : null);
    }
}
