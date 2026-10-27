using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace StreamFin;

public sealed class SearchHistory(ILibraryManager library)
{
    // Stored in item tags since AniFin 0.2. Renaming it would resurface every cleared result.
    public const string HiddenTag = "AniFin:ClearedSearchResult";
    private long generation;
    public long Generation => Interlocked.Read(ref generation);
    public static bool IsSearchResult(BaseItem? item) => item?.ExternalId is { } id && (item is Series or MediaBrowser.Controller.Entities.Movies.Movie
        && (id.StartsWith("title/search/", StringComparison.Ordinal) || id.StartsWith("tv/search/", StringComparison.Ordinal) || id.StartsWith("movie/search/", StringComparison.Ordinal))
        || item is MediaBrowser.Controller.Entities.Audio.MusicArtist or MediaBrowser.Controller.Entities.Audio.MusicAlbum or MediaBrowser.Controller.Entities.Audio.Audio
        && (id.StartsWith("artist/", StringComparison.Ordinal) || id.StartsWith("album/search/", StringComparison.Ordinal) || id.StartsWith("track/hit/", StringComparison.Ordinal)));
    public static bool IsHidden(BaseItem? item) => IsSearchResult(item) && item!.Tags.Contains(HiddenTag, StringComparer.Ordinal);
    public static Guid[] Folders(ILibraryManager library) =>
        [library.GetNewItemId("searchAnime16", typeof(Folder)), library.GetNewItemId(FilmSearch.FolderKey, typeof(Folder)), library.GetNewItemId(MusicSearch.FolderKey, typeof(Folder))];

    public async Task<int> ClearAsync(CancellationToken ct)
    {
        await ChannelNavigation.Gate.WaitAsync(ct);
        try
        {
            // Searches already in flight must not refill the list after this clear.
            Interlocked.Increment(ref generation);
            var channels = new[] { "Channel Anime", "Channel " + FilmChannel.ChannelName, "Channel " + MusicChannel.ChannelName }.Select(key => library.GetNewItemId(key, typeof(Channel))).ToArray();
            var items = Folders(library).SelectMany(parentId => library.GetItemList(new InternalItemsQuery
            {
                ParentId = parentId, Recursive = false, GroupByPresentationUniqueKey = false
            }));
            var count = 0;
            foreach (var item in items.Where(i => channels.Contains(i.ChannelId) && IsSearchResult(i) && !IsHidden(i)))
            {
                var previous = item.Tags;
                item.Tags = [.. previous, HiddenTag];
                try { await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct); }
                catch { item.Tags = previous; throw; }
                count++;
            }
            return count;
        }
        finally { ChannelNavigation.Gate.Release(); }
    }
}

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/SearchResults")]
public sealed class SearchHistoryController(SearchHistory history) : ControllerBase
{
    [HttpPost("Clear")]
    public async Task<ActionResult<ClearSearchResultsResponse>> Clear(CancellationToken ct)
        => Ok(new ClearSearchResultsResponse(await history.ClearAsync(ct)));
}

public sealed record ClearSearchResultsResponse(int ClearedCount);
