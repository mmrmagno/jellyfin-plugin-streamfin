using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace StreamFin;

// Policy is evaluated from metadata even for items cached before the filter changed.
public sealed class ContentAccess(ILibraryManager library, AniListCatalog catalog)
{
    private readonly Dictionary<int, bool> decisions = new();
    public Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(MediaBrowser.Controller.Channels.Channel));
    public Guid FilmChannelId => library.GetNewItemId("Channel " + FilmChannel.ChannelName, typeof(MediaBrowser.Controller.Channels.Channel));
    public bool IsOurs(Guid id) => id == ChannelId || id == FilmChannelId || library.GetItemById(id)?.ChannelId is { } channel && (channel == ChannelId || channel == FilmChannelId);

    private static int? AnimeId(BaseItem item)
    {
        var parts = item.ExternalId?.Split('/');
        return parts is { Length: >= 3 } && parts[0] is "title" or "source" or "episode" && int.TryParse(parts[2], out var id) ? id : null;
    }

    // A listing checks every row. Ask AniList about all of them at once.
    public Task PrefetchAsync(IEnumerable<Guid> ids, CancellationToken ct) => catalog.PrefetchAsync(ids
        .Select(library.GetItemById).OfType<BaseItem>().Where(item => item.ChannelId == ChannelId)
        .Select(AnimeId).OfType<int>().Where(id => !decisions.ContainsKey(id)), ct);

    public async Task<bool> AllowsAsync(Guid id, CancellationToken ct)
    {
        var item = library.GetItemById(id);
        if (item?.ChannelId == FilmChannelId) return FilmChannel.IsAvailable;
        if (item?.ChannelId != ChannelId) return true;
        var parts = item.ExternalId?.Split('/');
        if (parts is { Length: >= 4 } && parts[0] is "source" or "episode"
            && !SourceRegistry.IsEnabled(SourceRegistry.Split(parts[3]).Provider)) return false;
        if (AnimeId(item) is not { } animeId) return true;
        if (decisions.TryGetValue(animeId, out var allowed)) return allowed;
        try { return decisions[animeId] = AniListCatalog.IsAllowed(await catalog.GetAsync(animeId, ct)); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException) { return false; }
    }
}

public static class ChannelNavigation
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
