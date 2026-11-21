using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Entities;

namespace StreamFin;

public sealed partial class ChannelAccessFilter
{
    // Every anime site is its own "Season 1", and Jellyfin builds an episode queue from every season with that
    // number, so the episode after one could come from another site. These requests are answered here instead, with
    // the site's own episodes: for one site's season, or for the whole show one site after another. A request sorted
    // some other way, or made for another user, keeps Jellyfin's own answer.
    private Folder? QueueOf(ActionExecutingContext context, Guid me)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor { ControllerName: "TvShows", ActionName: "GetEpisodes" }) return null;
        // One sort field in newer Jellyfin, a list in older; either way a sorted request is Jellyfin's to answer.
        var sort = Argument<object>(context, "sortBy");
        if (sort is not null && !(sort is System.Collections.IEnumerable list and not string && !list.Cast<object>().Any())) return null;
        if (Argument<Guid?>(context, "userId") is { } asked && asked != Guid.Empty && asked != me) return null;
        if (Argument<Guid?>(context, "seasonId") is { } seasonId && seasonId != Guid.Empty)
            return library.GetItemById(seasonId) is Season { } season && season.ChannelId == content.ChannelId ? season : null;
        return Argument<Guid?>(context, "seriesId") is { } seriesId && library.GetItemById(seriesId) is Series { } series && series.ChannelId == content.ChannelId ? series : null;
    }

    private async Task<IReadOnlyList<Episode>> SeriesQueueAsync(Series series, Jellyfin.Database.Implementations.Entities.User user, CancellationToken ct)
    {
        if (series.ExternalId?.Split('/') is ["title", _, var animeText] && int.TryParse(animeText, out var animeId))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            try { await sites.SearchAsync(await anime.GetAsync(animeId, limit.Token), limit.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex)) { }
        }
        // The quickest working site's episodes come first, so playing the whole show starts on it.
        var folders = sites.Health.Fastest((await ListAsync(series, user, ct)).OfType<Season>().Where(s => s.ExternalId.StartsWith("source/", StringComparison.Ordinal)),
            s => SourceRegistry.Split(s.ExternalId.Split('/', 4).Last()).Provider).ToArray();
        await WarmAsync(folders, ct);
        var episodes = new List<Episode>();
        foreach (var site in folders) episodes.AddRange(ChannelNavigation.SiteEpisodes(await ListAsync(site, user, ct)));
        return episodes;
    }

    // The channel lock is shared by every listing on the server, so the sites are asked first, all at once and
    // outside it; the listings inside then answer from the cache.
    private async Task WarmAsync(IReadOnlyList<Season> folders, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        await Task.WhenAll(folders.Select(async folder =>
        {
            if (folder.ExternalId.Split('/', 4) is not ["source", _, var animeText, var key] || !int.TryParse(animeText, out var animeId)) return;
            try { await sites.EpisodesAsync(key, await anime.GetAsync(animeId, limit.Token), limit.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsSiteFailure(ex)) { }
        }));
    }

    private static bool IsSiteFailure(Exception ex) => ex is ProviderException or HttpRequestException or OperationCanceledException
        or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException;

    // The channel's own listing of a folder, which also creates its items. Jellyfin does not record every channel
    // episode's season, so this, not the library, says what a site's season holds.
    private async Task<IReadOnlyList<BaseItem>> ListAsync(Folder item, Jellyfin.Database.Implementations.Entities.User? user, CancellationToken ct)
    {
        await ChannelNavigation.Gate.WaitAsync(ct);
        try
        {
            var listed = (await channels.GetChannelItemsInternal(new InternalItemsQuery(user) { Parent = item, ChannelIds = [IsOurChannel(item.Id) ? item.Id : item.ChannelId] },
                new Progress<double>(), ct)).Items;
            // Search series received distinct grouping keys in 0.2, but Jellyfin does not refresh existing channel
            // descendants on every listing. Native Shows/Episodes queries use this stored key, unlike ParentId
            // listings. Repair metadata only; keep IDs and user data unchanged.
            var series = item as Series ?? (item as Season)?.Series;
            if (series is not null && !string.IsNullOrEmpty(series.PresentationUniqueKey))
            {
                foreach (var child in listed.Concat(library.GetItemList(new InternalItemsQuery { ParentId = item.Id, Recursive = false })).DistinctBy(c => c.Id))
                {
                    if (child is Season season && season.SeriesPresentationUniqueKey != series.PresentationUniqueKey)
                        season.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
                    else if (child is Episode episode && episode.SeriesPresentationUniqueKey != series.PresentationUniqueKey)
                        episode.SeriesPresentationUniqueKey = series.PresentationUniqueKey;
                    else continue;
                    await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
                }
            }
            return listed;
        }
        finally { ChannelNavigation.Gate.Release(); }
    }
}
