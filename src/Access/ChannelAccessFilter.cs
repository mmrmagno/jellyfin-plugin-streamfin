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

// IChannel.IsEnabledFor hides the channel, but cached descendants can also be requested
// via Jellyfin's generic item/playback endpoints. Protect those routes and their results.
public sealed class ChannelAccessFilter(ILibraryManager library, IAuthorizationContext authorization, IChannelManager channels, ArtworkAccess artwork, ContentAccess content,
    MusicLyrics lyrics, MusicMix mix, MediaBrowser.Controller.Dto.IDtoService dtos, AniListCatalog anime, SourceRegistry sites, OnlineSearch animeSearch, FilmSearch filmSearch,
    StreamingNavigation navigation, LibraryStreamingLinks links)
    : IAsyncActionFilter, IAsyncResultFilter
{
    private bool IsOurs(Guid id) => id != Guid.Empty && content.IsOurs(id);
    private bool IsOurChannel(Guid id) => content.IsChannel(id);
    private Task<bool> Allowed(ActionContext context) => artwork.AllowsAsync(context.HttpContext);
    private void Countable(BaseItemDto dto)
    {
        // A season without its series link makes Jellyfin's app request Shows/null/Episodes, which fails (HTTP 400).
        // Some saved seasons have no series id at all; their parent folder is the series.
        if (dto.Type == BaseItemKind.Season && (dto.SeriesId is null || dto.SeriesId == Guid.Empty) && IsOurs(dto.Id) && library.GetItemById(dto.Id) is Season season)
        {
            var series = season.SeriesId != Guid.Empty ? season.SeriesId : library.GetItemById(season.ParentId) is Series parent ? parent.Id : Guid.Empty;
            if (series != Guid.Empty) { dto.SeriesId = series; dto.SeriesName ??= library.GetItemById(series)?.Name; }
        }
        // Jellyfin reports every channel folder except series and seasons as ChannelFolderItem, which
        // clients file under no heading. Search and detail pages need the album or artist type.
        if (dto.Type == BaseItemKind.ChannelFolderItem && IsOurs(dto.Id))
            dto.Type = library.GetItemById(dto.Id) switch { MusicAlbum => BaseItemKind.MusicAlbum, MusicArtist => BaseItemKind.MusicArtist, _ => dto.Type };
        // Players only offer lyrics when this is set; the lyrics route below finds them on demand.
        if (dto.Type == BaseItemKind.Audio && MusicChannel.IsAvailable && IsOurs(dto.Id)) dto.HasLyrics = true;
        if (dto.Type is not (BaseItemKind.Season or BaseItemKind.MusicAlbum or BaseItemKind.MusicArtist) || dto.ChildCount > 0 || !IsOurs(dto.Id)) return;
        dto.ChildCount = Math.Max(1, library.GetCount(new InternalItemsQuery { ParentId = dto.Id, Recursive = false }));
    }
    // What the app asked to have filled in, as Jellyfin's own episode listing reads it.
    private static MediaBrowser.Controller.Dto.DtoOptions Options(IDictionary<string, object?> arguments)
    {
        T? Get<T>(string name) => arguments.TryGetValue(name, out var value) && value is T typed ? typed : default;
        var options = new MediaBrowser.Controller.Dto.DtoOptions { Fields = Get<ItemFields[]>("fields") ?? [] };
        if (Get<bool?>("enableImages") is { } images) options.EnableImages = images;
        if (Get<bool?>("enableUserData") is { } userData) options.EnableUserData = userData;
        if (Get<int?>("imageTypeLimit") is { } limit) options.ImageTypeLimit = limit;
        if (Get<ImageType[]>("enableImageTypes") is { Length: > 0 } types) options.ImageTypes = types;
        return options;
    }

    private static IEnumerable<Guid> Identifiers(object? value, int depth = 0)
    {
        if (value is Guid guid) { yield return guid; yield break; }
        if (value is string text)
        {
            foreach (var part in text.Split(',')) if (Guid.TryParse(part, out var id)) yield return id;
            yield break;
        }
        if (value is System.Collections.IEnumerable list)
        {
            foreach (var item in list) foreach (var id in Identifiers(item, depth + 1)) yield return id;
            yield break;
        }
        if (value is null || depth > 1) yield break;
        foreach (var property in value.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0
                     && (p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase) || p.Name.EndsWith("Ids", StringComparison.OrdinalIgnoreCase))))
            foreach (var id in Identifiers(property.GetValue(value), depth + 1)) yield return id;
    }
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ids = context.ActionArguments.Values.SelectMany(v => Identifiers(v))
            .Concat(context.HttpContext.Request.Query.SelectMany(v => Identifiers(v.Value.ToString()))).Distinct().ToArray();
        context.HttpContext.Items[SearchHistory.HiddenTag] = SearchHistory.Folders(library).Any(ids.Contains);
        if (!await Allowed(context))
        {
            if (ids.Any(IsOurs)) { context.Result = new NotFoundResult(); return; }
        }
        else
        {
            // Image requests can be anonymous, so they must not spend the AniList budget.
            var image = ArtworkAccess.IsImage(context.HttpContext);
            foreach (var id in ids.Where(IsOurs))
                if (!await content.AllowsAsync(id, context.HttpContext.RequestAborted, cachedOnly: image)) { context.Result = new NotFoundResult(); return; }
            if (context.HttpContext.Request.Method == "GET" && context.ActionDescriptor is ControllerActionDescriptor browse && CatalogSearch.IsBrowseAction(browse.ActionName)
                && await CatalogSearchAsync(context) is { } catalogResult)
            {
                context.Result = catalogResult;
                return;
            }
            if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "Lyrics", ActionName: "GetLyrics" }
                && context.ActionArguments.TryGetValue("itemId", out var lyricItem) && lyricItem is Guid lyricId && IsOurs(lyricId))
            {
                context.Result = library.GetItemById(lyricId) is Audio audio && await lyrics.ForItemAsync(audio, context.HttpContext.RequestAborted) is { } found
                    ? new OkObjectResult(found) : new NotFoundResult();
                return;
            }
            if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "InstantMix" }
                && ids.Where(IsOurs).Select(library.GetItemById).FirstOrDefault(i => i is MusicArtist or MusicAlbum or Audio) is { } seed)
            {
                var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                var limit = int.TryParse(context.HttpContext.Request.Query["limit"], out var asked) ? Math.Clamp(asked, 1, 50) : 50;
                QueryResult<BaseItemDto>? mixed;
                try { mixed = auth.User is null ? null : await mix.MixAsync(seed, auth.User, limit, context.HttpContext.RequestAborted); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException
                    or TaskCanceledException && !context.HttpContext.RequestAborted.IsCancellationRequested) { mixed = new QueryResult<BaseItemDto> { Items = [], TotalRecordCount = 0 }; }
                if (mixed is not null) { context.Result = new OkObjectResult(mixed); return; }
            }
            // Native series/season screens use the Shows API instead of Channel/Items.
            // Populate those children lazily before Jellyfin answers the normal request.
            var path = context.HttpContext.Request.Path.Value ?? "";
            if (context.HttpContext.Request.Method == "GET" && !path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
                && (path.StartsWith("/Shows/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase)))
            {
                var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                var ct = context.HttpContext.RequestAborted;
                var queueOf = auth.User is null ? null : QueueOf(context, auth.User.Id);
                foreach (var id in ids)
                {
                    if (id == queueOf?.Id) continue;
                    var item = library.GetItemById(id);
                    if (item is not Folder folder || (!IsOurChannel(item.ChannelId) && !IsOurChannel(item.Id))) continue;
                    await ListAsync(folder, auth.User, ct);
                }
                if (queueOf is not null)
                {
                    if (queueOf is Season site) await WarmAsync([site], ct);
                    var all = queueOf is Season one ? ChannelNavigation.SiteEpisodes(await ListAsync(one, auth.User, ct))
                        : await SeriesQueueAsync((Series)queueOf, auth.User!, ct);
                    // Jellyfin's season and missing filters apply to the queue too. StreamFin has no missing episodes.
                    if (Argument<bool?>(context, "isMissing") is true) all = [];
                    else if ((Argument<int?>(context, "seasonNumber") ?? Argument<int?>(context, "season")) is { } seasonNumber)
                        all = all.Where(e => e.ParentIndexNumber == seasonNumber).ToArray();
                    IEnumerable<Episode> queue = all;
                    if (Argument<Guid?>(context, "startItemId") is { } startId && startId != Guid.Empty) queue = queue.SkipWhile(e => e.Id != startId);
                    if (Argument<Guid?>(context, "adjacentTo") is { } around && around != Guid.Empty)
                    {
                        var at = all.ToList().FindIndex(e => e.Id == around);
                        queue = at < 0 ? [] : all.Skip(Math.Max(0, at - 1)).Take(at == 0 ? 2 : 3);
                    }
                    var listed = queue.ToArray();
                    var skip = Math.Max(0, Argument<int?>(context, "startIndex") ?? 0);
                    var page = listed.Skip(skip);
                    if (Argument<int?>(context, "limit") is { } take) page = page.Take(Math.Max(0, take));
                    context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                    {
                        Items = dtos.GetBaseItemDtos(page.ToArray(), Options(context.ActionArguments), auth.User),
                        TotalRecordCount = listed.Length, StartIndex = skip
                    });
                    return;
                }
            }
            if (context.HttpContext.Request.Method == "GET" && Plugin.Instance?.Configuration.StreamingLibraryLinks is { Count: > 0 }
                && (await authorization.GetAuthorizationInfo(context.HttpContext)).User is { } viewer)
                await links.PrepareAsync(context, viewer, context.HttpContext.RequestAborted);
        }
        await next();
    }
    // A search started on a catalog page keeps that page as its parent, so Jellyfin's own query would list the whole
    // catalog. These searches ask the matching provider instead, for the kinds the row asked for.
    private async Task<IActionResult?> CatalogSearchAsync(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;
        var parentId = context.ActionArguments.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase))
            .SelectMany(p => Identifiers(p.Value)).FirstOrDefault();
        if (parentId == Guid.Empty)
            parentId = request.Query.Where(p => p.Key.Equals("parentId", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("folderId", StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => Identifiers(p.Value.ToString())).FirstOrDefault();
        if (parentId == Guid.Empty || library.GetItemById(parentId) is not Folder parent || !IsOurs(parentId) || !CatalogSearch.IsCatalog(parent.ExternalId)) return null;
        var term = (Argument<string>(context, "searchTerm") ?? request.Query["searchTerm"].ToString()).Trim();
        if (term.Length is < 2 or > 100) return null;
        var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
        if (auth.User is null || !Plugin.Allows(auth.UserId)) return null;
        var include = Argument<BaseItemKind[]>(context, "includeItemTypes") ?? [];
        var exclude = Argument<BaseItemKind[]>(context, "excludeItemTypes") ?? [];
        var media = Argument<MediaType[]>(context, "mediaTypes") ?? [];
        var start = Math.Max(0, int.TryParse(request.Query["StartIndex"], out var offset) ? offset : 0);
        int? limit = int.TryParse(request.Query["Limit"], out var size) && size > 0 ? Math.Min(size, 200) : null;
        var kinds = CatalogSearch.Kinds(parent.ExternalId!.StartsWith("catalog/", StringComparison.Ordinal), include, exclude);
        // With online search off the results would be hidden anyway, so the sites are not asked.
        if (kinds.Length == 0 || (media.Length > 0 && !media.Contains(MediaType.Video)) || Plugin.Instance?.Configuration.OnlineSearch == false)
            return new OkObjectResult(new QueryResult<BaseItemDto> { Items = [], TotalRecordCount = 0, StartIndex = start });
        var query = new SearchProviderQuery { SearchTerm = term, UserId = auth.UserId, IncludeItemTypes = kinds, ExcludeItemTypes = exclude, MediaTypes = media, Limit = 30 };
        var ct = context.HttpContext.RequestAborted;
        var found = parent.ExternalId.StartsWith("catalog/", StringComparison.Ordinal) ? await animeSearch.SearchAsync(query, ct) : await filmSearch.SearchAsync(query, ct);
        var items = found.Select(result => library.GetItemById(result.ItemId)).OfType<BaseItem>().ToArray();
        var page = items.Skip(start);
        if (limit is { } take) page = page.Take(take);
        return new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = dtos.GetBaseItemDtos(page.ToArray(), Options(context.ActionArguments), auth.User), TotalRecordCount = items.Length, StartIndex = start
        });
    }

    private static T? Argument<T>(ActionExecutingContext context, string name) =>
        context.ActionArguments.FirstOrDefault(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is T value ? value : default;

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

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var allowed = await Allowed(context);
        var discoveryList = context.HttpContext.Items[SearchHistory.HiddenTag] is true;
        // With online search switched off, StreamFin titles are hidden from every search: saved search results, and
        // catalog titles Jellyfin finds in its own database. Browsing the channels is unaffected.
        var onlineSearchOff = Plugin.Instance?.Configuration.OnlineSearch == false;
        var searching = !string.IsNullOrWhiteSpace(context.HttpContext.Request.Query["searchTerm"].ToString());
        async Task<bool> Visible(Guid id, bool listing = true) => !IsOurs(id) || (allowed
            && (!listing || !discoveryList || !SearchHistory.IsHidden(library.GetItemById(id)))
            && !(onlineSearchOff && searching)
            && await content.AllowsAsync(id, context.HttpContext.RequestAborted));
        // The StreamFin entry's folders become the channels they lead to. In Hub mode the separate channels are left out of
        // the menu and home screen only; their ids keep working for the hub, favourites, history and playback.
        var rootListing = context.ActionDescriptor is ControllerActionDescriptor { ActionName: "GetChannels" or "GetUserViews" };
        Jellyfin.Database.Implementations.Entities.User? viewer = null;
        async Task<BaseItemDto?> Navigate(BaseItemDto dto)
        {
            if (rootListing && navigation.HidesChannel(dto.Id)) return null;
            if (!navigation.IsPlaceholder(dto.Id)) return dto;
            viewer ??= allowed ? (await authorization.GetAuthorizationInfo(context.HttpContext)).User : null;
            return await navigation.ResolveAsync(dto, viewer, context.HttpContext.RequestAborted);
        }
        if (context.Result is ObjectResult result)
        {
            if (allowed) await content.PrefetchAsync(result.Value switch
            {
                QueryResult<BaseItemDto> query => query.Items.Select(dto => dto.Id),
                IEnumerable<BaseItemDto> items => items.Select(dto => dto.Id),
                SearchHintResult hints => hints.SearchHints.Select(hint => hint.Id),
                _ => []
            }, context.HttpContext.RequestAborted);
            switch (result.Value)
            {
                case BaseItemDto item:
                    if (await Navigate(item) is not { } shown || !await Visible(shown.Id, listing: false)) context.Result = new NotFoundResult();
                    else { Countable(shown); result.Value = shown; }
                    break;
                case QueryResult<BaseItemDto> query:
                    if (allowed) LibraryStreamingLinks.Apply(query, context.HttpContext);
                    var kept = new List<BaseItemDto>();
                    foreach (var dto in query.Items) if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); kept.Add(mapped); }
                    var filtered = kept.ToArray();
                    query.TotalRecordCount = Math.Max(filtered.Length, query.TotalRecordCount - (query.Items.Count - filtered.Length));
                    query.Items = filtered;
                    break;
                case IEnumerable<BaseItemDto> items:
                    var visible = new List<BaseItemDto>();
                    foreach (var dto in items) if (await Navigate(dto) is { } mapped && await Visible(mapped.Id)) { Countable(mapped); visible.Add(mapped); }
                    result.Value = visible.ToArray();
                    break;
                // Remote-control users see what others are playing; hide StreamFin titles they may not see.
                case IEnumerable<SessionInfoDto> sessions:
                    foreach (var session in sessions)
                    {
                        if (session.NowPlayingItem is { } playing && !await Visible(playing.Id, listing: false)) { session.NowPlayingItem = null; session.NowPlayingQueue = []; }
                        if (session.NowViewingItem is { } viewing && !await Visible(viewing.Id, listing: false)) session.NowViewingItem = null;
                    }
                    break;
                case SearchHintResult hints:
                    var safeHints = new List<SearchHint>();
                    foreach (var hint in hints.SearchHints) if (await Visible(hint.Id)) safeHints.Add(hint);
                    result.Value = new SearchHintResult(safeHints, Math.Max(safeHints.Count, hints.TotalRecordCount - hints.SearchHints.Count + safeHints.Count));
                    break;
            }
        }
        await next();
    }
}
