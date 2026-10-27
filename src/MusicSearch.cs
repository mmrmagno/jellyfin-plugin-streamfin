using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public sealed class MusicSearch(MusicCatalog catalog, ILibraryManager library, SearchHistory history, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILogger<MusicSearch> logger) : IInternalSearchProvider
{
    // Jellyfin derives channel item IDs from external ID + channel name + "16".
    public const string FolderKey = "search" + MusicChannel.ChannelName + "16";
    public string Name => "StreamFin Music";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 52;
    public Guid ChannelId => library.GetNewItemId("Channel " + MusicChannel.ChannelName, typeof(Channel));
    public Guid SearchFolderId => library.GetNewItemId(FolderKey, typeof(Folder));
    private static bool Wants(SearchProviderQuery query, BaseItemKind kind) => query.IncludeItemTypes.Length == 0
        ? !query.ExcludeItemTypes.Contains(kind) : query.IncludeItemTypes.Contains(kind);
    public bool CanSearch(SearchProviderQuery query) => query.SearchTerm.Trim().Length is >= 2 and <= 100
        && (Wants(query, BaseItemKind.MusicArtist) || Wants(query, BaseItemKind.MusicAlbum) || Wants(query, BaseItemKind.Audio))
        && (query.MediaTypes.Length == 0 || query.MediaTypes.Contains(MediaType.Audio))
        && (!query.ParentId.HasValue || query.ParentId == Guid.Empty || query.ParentId == ChannelId || query.ParentId == SearchFolderId);

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        if (!CanSearch(query) || !MusicChannel.IsAvailable || contexts.HttpContext is not { } context) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || (query.UserId is { } user && user != Guid.Empty && user != auth.UserId)) return [];
        try
        {
            var generation = history.Generation;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var results = await catalog.SearchAsync(query.SearchTerm, timeout.Token);
            if (MusicCatalog.PreferExplicit)
                results = results with
                {
                    Tracks = Wants(query, BaseItemKind.Audio) ? await catalog.ExplicitTracksAsync(results.Tracks, timeout.Token) : results.Tracks,
                    // Telling album copies apart costs a release lookup each, so only when albums are wanted.
                    Releases = Wants(query, BaseItemKind.MusicAlbum) ? await catalog.WithoutCleanCopiesAsync(results.Releases, timeout.Token) : results.Releases
                };
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
                var limit = Math.Clamp(query.Limit ?? 20, 1, 20);
                var found = new List<SearchResult>();
                async Task AddAsync<T>(ChannelItemInfo info, Action<T> fill) where T : BaseItem, new()
                {
                    var id = library.GetNewItemId(info.Id + MusicChannel.ChannelName + "16", typeof(T));
                    var existing = library.GetItemById(id) as T;
                    var item = existing ?? new T { Id = id, DateCreated = DateTime.UtcNow };
                    item.ExternalId = info.Id;
                    item.ChannelId = ChannelId;
                    item.ParentId = parent.Id;
                    item.Name = info.Name;
                    item.Overview = info.Overview;
                    item.ProductionYear = info.ProductionYear;
                    item.PremiereDate = info.PremiereDate;
                    item.Tags = item.Tags.Where(tag => tag != SearchHistory.HiddenTag).Append("StreamFin").Distinct().ToArray();
                    item.ProviderIds = info.ProviderIds;
                    // Generic search groups by this key; a shared NULL would collapse unrelated results.
                    item.PresentationUniqueKey = "streamfin-search-" + info.Id;
                    fill(item);
                    if (info.ImageUrl is not null && !item.HasImage(ImageType.Primary)) item.SetImagePath(ImageType.Primary, info.ImageUrl);
                    item.OnMetadataChanged();
                    if (existing is null) library.CreateItem(item, parent);
                    else await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, timeout.Token);
                    found.Add(new SearchResult(id, 80 - found.Count));
                }
                if (Wants(query, BaseItemKind.MusicArtist))
                    foreach (var artist in results.Artists.Take(limit)) await AddAsync<MusicArtist>(MusicChannel.Artist(artist), _ => { });
                if (Wants(query, BaseItemKind.MusicAlbum))
                    foreach (var release in results.Releases.Take(limit))
                    {
                        var info = MusicChannel.Album(release, "search");
                        await AddAsync<MusicAlbum>(info, album => { album.AlbumArtists = info.AlbumArtists; album.Artists = info.Artists; });
                    }
                if (Wants(query, BaseItemKind.Audio))
                    foreach (var track in results.Tracks.Take(limit))
                    {
                        var info = MusicChannel.Track(track, "hit");
                        await AddAsync<Audio>(info, audio =>
                        {
                            audio.Artists = info.Artists;
                            audio.AlbumArtists = info.AlbumArtists;
                            audio.RunTimeTicks = info.RunTimeTicks;
                        });
                    }
                return found;
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin music search unavailable ({ErrorType}); local search remains available", ex.GetType().Name);
            return [];
        }
    }
}
