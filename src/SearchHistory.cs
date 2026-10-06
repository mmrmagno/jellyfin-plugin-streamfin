using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AniFin;

// Keep item IDs and their user data intact. Only the discovery list is cleared.
public sealed class SearchHistory(ILibraryManager library)
{
    public const string HiddenTag = "AniFin:ClearedSearchResult";
    private long generation;
    public long Generation => Interlocked.Read(ref generation);
    public static bool IsHidden(BaseItem? item) => item is Series
        && item.ExternalId?.StartsWith("title/search/", StringComparison.Ordinal) == true
        && item.Tags.Contains(HiddenTag, StringComparer.Ordinal);

    public async Task<int> ClearAsync(CancellationToken ct)
    {
        await ChannelNavigation.Gate.WaitAsync(ct);
        try
        {
            // Searches already in flight must not refill the list after this clear.
            Interlocked.Increment(ref generation);
            var channelId = library.GetNewItemId("Channel Anime", typeof(Channel));
            var parentId = library.GetNewItemId("searchAnime16", typeof(Folder));
            var items = library.GetItemList(new InternalItemsQuery
            {
                ParentId = parentId, Recursive = false, GroupByPresentationUniqueKey = false
            });
            var count = 0;
            foreach (var item in items.OfType<Series>().Where(i => i.ChannelId == channelId
                         && i.ExternalId?.StartsWith("title/search/", StringComparison.Ordinal) == true && !IsHidden(i)))
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
[Route("AniFin/SearchResults")]
public sealed class SearchHistoryController(SearchHistory history) : ControllerBase
{
    [HttpPost("Clear")]
    public async Task<ActionResult<ClearSearchResultsResponse>> Clear(CancellationToken ct)
        => Ok(new ClearSearchResultsResponse(await history.ClearAsync(ct)));
}

public sealed record ClearSearchResultsResponse(int ClearedCount);
