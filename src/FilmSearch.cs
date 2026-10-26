using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public sealed class FilmSearch(TmdbCatalog catalog, ILibraryManager library, SearchHistory history, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILogger<FilmSearch> logger) : IInternalSearchProvider
{
    // Jellyfin derives channel item IDs from external ID + channel name + "16".
    public const string FolderKey = "search" + FilmChannel.ChannelName + "16";
    public string Name => "StreamFin Movies & TV";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 51;
    public Guid ChannelId => library.GetNewItemId("Channel " + FilmChannel.ChannelName, typeof(Channel));
    public Guid SearchFolderId => library.GetNewItemId(FolderKey, typeof(Folder));
    private static bool Wants(SearchProviderQuery query, BaseItemKind kind) => query.IncludeItemTypes.Length == 0
        ? !query.ExcludeItemTypes.Contains(kind) : query.IncludeItemTypes.Contains(kind);
    public bool CanSearch(SearchProviderQuery query) => query.SearchTerm.Trim().Length is >= 2 and <= 100
        && (Wants(query, BaseItemKind.Movie) || Wants(query, BaseItemKind.Series))
        && (query.MediaTypes.Length == 0 || query.MediaTypes.Contains(MediaType.Video))
        && (!query.ParentId.HasValue || query.ParentId == Guid.Empty || query.ParentId == ChannelId || query.ParentId == SearchFolderId);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        if (!CanSearch(query) || !FilmChannel.IsAvailable || contexts.HttpContext is not { } context) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || (query.UserId is { } user && user != Guid.Empty && user != auth.UserId)) return [];
        try
        {
            var generation = history.Generation;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var results = await catalog.BrowseAsync("search", 1, timeout.Token, query.SearchTerm);
            await ChannelNavigation.Gate.WaitAsync(timeout.Token);
            try
            {
                // A search that started before Clear must not refill the list.
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
                foreach (var film in results.Items.Where(f => Wants(query, f.Kind == FilmKind.Movie ? BaseItemKind.Movie : BaseItemKind.Series))
                             .Take(Math.Clamp(query.Limit ?? 20, 1, 20)))
                {
                    var externalId = FilmChannel.Item(film, "search").Id;
                    var key = externalId + FilmChannel.ChannelName + "16";
                    var id = film.Kind == FilmKind.Movie ? library.GetNewItemId(key, typeof(Movie)) : library.GetNewItemId(key, typeof(Series));
                    var existing = library.GetItemById(id);
                    BaseItem item = existing ?? (film.Kind == FilmKind.Movie ? new Movie() : new Series());
                    item.Id = id;
                    item.ExternalId = externalId;
                    item.ChannelId = ChannelId;
                    item.ParentId = parent.Id;
                    if (existing is null) item.DateCreated = DateTime.UtcNow;
                    item.Name = film.Title;
                    item.Overview = film.Overview;
                    item.ProductionYear = film.Year;
                    item.Tags = ["StreamFin"];
                    item.ProviderIds = FilmChannel.Ids(film);
                    // Generic search groups by this key; a shared NULL would collapse unrelated titles.
                    item.PresentationUniqueKey = $"streamfin-search-{film.Kind.ToString().ToLowerInvariant()}-{film.Id}";
                    if (film.Runtime is > 0) item.RunTimeTicks = TimeSpan.FromMinutes(film.Runtime.Value).Ticks;
                    if (film.Image is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, film.Image);
                    item.OnMetadataChanged();
                    if (existing is null) library.CreateItem(item, parent);
                    else await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, timeout.Token);
                    found.Add(new SearchResult(id, 90 - found.Count));
                }
                return found;
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin film search unavailable ({ErrorType}); local search remains available", ex.GetType().Name);
            return [];
        }
    }
}
