using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace AniFin;

// Policy is evaluated from metadata even for items cached before the filter changed.
public sealed class ContentAccess(ILibraryManager library, AniListCatalog catalog)
{
    private readonly Dictionary<int, bool> decisions = new();
    public Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(MediaBrowser.Controller.Channels.Channel));
    public bool IsOurs(Guid id) => id == ChannelId || library.GetItemById(id)?.ChannelId == ChannelId;
    public async Task<bool> AllowsAsync(Guid id, CancellationToken ct)
    {
        var item = library.GetItemById(id);
        if (item?.ChannelId != ChannelId) return true;
        var parts = item.ExternalId?.Split('/');
        if (parts is not { Length: >= 3 } || parts[0] is not ("title" or "source" or "episode") || !int.TryParse(parts[2], out var animeId)) return true;
        if (decisions.TryGetValue(animeId, out var allowed)) return allowed;
        try { return decisions[animeId] = AniListCatalog.IsAllowed(await catalog.GetAsync(animeId, ct)); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException) { return false; }
    }
}

public static class ChannelNavigation
{
    public static readonly SemaphoreSlim Gate = new(1, 1);
}
