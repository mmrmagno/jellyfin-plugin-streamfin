using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StreamFin;

public sealed class ContentAccess(ILibraryManager library, AniListCatalog catalog)
{
    private readonly Dictionary<int, bool> decisions = new();
    public Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid FilmChannelId => library.GetNewItemId("Channel " + FilmChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid MusicChannelId => library.GetNewItemId("Channel " + MusicChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    // Guid.Empty comes from requests without a parent or start item; GetItemById throws on it.
    public bool IsOurs(Guid id) => id != Guid.Empty && (IsChannel(id) || library.GetItemById(id)?.ChannelId is { } channel && IsChannel(channel));
    public bool IsChannel(Guid id) => id == ChannelId || id == FilmChannelId || id == MusicChannelId;

    private static int? AnimeId(BaseItem item)
    {
        var parts = item.ExternalId?.Split('/');
        return parts is { Length: >= 3 } && parts[0] is "title" or "source" or "episode" && int.TryParse(parts[2], out var id) ? id : null;
    }

    public Task PrefetchAsync(IEnumerable<Guid> ids, CancellationToken ct) => catalog.PrefetchAsync(ids
        .Where(id => id != Guid.Empty).Select(library.GetItemById).OfType<BaseItem>().Where(item => item.ChannelId == ChannelId)
        .Select(AnimeId).OfType<int>().Where(id => !decisions.ContainsKey(id)), ct);

    // cachedOnly answers from what is already cached without asking AniList; unknown titles are denied.
    public async Task<bool> AllowsAsync(Guid id, CancellationToken ct, bool cachedOnly = false)
    {
        if (id == Guid.Empty) return true;
        var item = library.GetItemById(id);
        if (item?.ChannelId == FilmChannelId) return FilmChannel.IsAvailable;
        if (item?.ChannelId == MusicChannelId) return MusicChannel.IsAvailable;
        if (item?.ChannelId != ChannelId) return true;
        var parts = item.ExternalId?.Split('/');
        if (parts is { Length: >= 4 } && parts[0] is "source" or "episode"
            && !SourceRegistry.IsEnabled(SourceRegistry.Split(parts[3]).Provider)) return false;
        if (AnimeId(item) is not { } animeId) return true;
        if (decisions.TryGetValue(animeId, out var allowed)) return allowed;
        if (cachedOnly) return catalog.Cached(animeId) is { } known && AniListCatalog.IsAllowed(known);
        try { return decisions[animeId] = AniListCatalog.IsAllowed(await catalog.GetAsync(animeId, ct)); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException) { return false; }
    }
}

public static class ChannelNavigation
{
    public static readonly SemaphoreSlim Gate = new(1, 1);

    // By the number the site gave ("12.5" recaps between 12 and 13), in the site's order otherwise.
    public static IReadOnlyList<Episode> SiteEpisodes(IEnumerable<BaseItem> children) => children.OfType<Episode>()
        .Select((e, index) => (e, index)).OrderBy(p => AnimeChannel.Number(p.e.Name) ?? p.e.IndexNumber ?? decimal.MaxValue).ThenBy(p => p.index)
        .Select(p => p.e).ToArray();
}

// Jellyfin treats an item with no runtime as fully played and clears its resume point, so the first probed runtime is kept.
public static class ResumeRuntime
{
    public static async Task KeepAsync(ILibraryManager library, string key, Type type, long? ticks, CancellationToken ct)
    {
        if (ticks is not > 0 || library.GetItemById(library.GetNewItemId(key, type)) is not { } item || item.RunTimeTicks is > 0) return;
        item.RunTimeTicks = ticks;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
    }
}
