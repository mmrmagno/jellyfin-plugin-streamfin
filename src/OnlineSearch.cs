using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Register alongside the internal providers so results merge with the local library.
// Jellyfin's external-provider path replaces local results whenever it finds a match.
public sealed class OnlineSearch(AniListCatalog catalog, ILibraryManager library, SearchHistory history, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILogger<OnlineSearch> logger) : IInternalSearchProvider
{
    public string Name => "StreamFin";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 50;
    public Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(Channel));
    public Guid SearchFolderId => library.GetNewItemId("searchAnime16", typeof(Folder));
    public bool CanSearch(SearchProviderQuery query) => query.SearchTerm.Trim().Length is >= 2 and <= 100
        && (query.IncludeItemTypes.Length == 0 ? !query.ExcludeItemTypes.Contains(BaseItemKind.Series) : query.IncludeItemTypes.Contains(BaseItemKind.Series))
        && (query.MediaTypes.Length == 0 || query.MediaTypes.Contains(MediaType.Video))
        && (!query.ParentId.HasValue || query.ParentId == Guid.Empty || query.ParentId == ChannelId || query.ParentId == SearchFolderId);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        if (!CanSearch(query) || contexts.HttpContext is not { } context) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !SourceRegistry.AnyEnabled
            || (query.UserId is { } user && user != Guid.Empty && user != auth.UserId)) return [];
        try
        {
            var generation = history.Generation;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var results = await catalog.BrowseAsync("search", 1, timeout.Token, query.SearchTerm);
            await ChannelNavigation.Gate.WaitAsync(timeout.Token);
            try
            {
                if (generation != history.Generation) return [];
                if (library.GetItemById(ChannelId) is not Channel channel) return [];
                var parent = library.GetItemById(SearchFolderId) as Folder;
                if (parent is null)
                {
                    parent = new Folder { Id = SearchFolderId, Name = "Search results", ExternalId = "search", ChannelId = ChannelId, ParentId = ChannelId, Tags = ["StreamFin"] };
                    parent.OnMetadataChanged();
                    library.CreateItem(parent, channel);
                }
                var found = new List<SearchResult>();
                foreach (var anime in results.Items.Take(Math.Clamp(query.Limit ?? 30, 1, 30)))
                {
                    if (!AniListCatalog.IsAllowed(anime)) continue;
                    var externalId = $"title/search/{anime.Id}";
                    var id = library.GetNewItemId(externalId + "Anime16", typeof(Series));
                    var item = library.GetItemById(id) as Series;
                    var isNew = item is null;
                    item ??= new Series { Id = id, ExternalId = externalId, ChannelId = ChannelId, ParentId = parent.Id, DateCreated = DateTime.UtcNow };
                    item.Name = anime.Title;
                    item.OriginalTitle = anime.Aliases.FirstOrDefault();
                    item.Overview = anime.Overview;
                    item.ProductionYear = anime.Year;
                    item.Genres = anime.Genres;
                    item.Tags = item.Tags.Where(tag => tag != SearchHistory.HiddenTag).Append("StreamFin").Distinct().ToArray();
                    item.ProviderIds = new Dictionary<string, string> { ["Custom"] = $"anifin-series-{anime.Id}" };
                    // Generic search groups series by this persisted key; NULL would collapse unrelated anime.
                    item.PresentationUniqueKey = $"anifin-search-{anime.Id}";
                    if (anime.Image is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, anime.Image);
                    item.OnMetadataChanged();
                    if (isNew) library.CreateItem(item, parent);
                    else await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, timeout.Token);
                    found.Add(new SearchResult(id, 100 - found.Count));
                }
                return found;
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        // Apps search as you type and cancel the previous search with each letter; that is not a failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin online search unavailable ({ErrorType}); local search remains available", ex.GetType().Name);
            return [];
        }
    }
}
