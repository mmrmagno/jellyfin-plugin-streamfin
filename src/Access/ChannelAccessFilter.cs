using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Model.Search;
using MediaBrowser.Model.Entities;

namespace StreamFin;

// IChannel.IsEnabledFor hides the channel, but cached descendants can also be requested
// via Jellyfin's generic item/playback endpoints. Protect those routes and their results.
public sealed partial class ChannelAccessFilter(ILibraryManager library, IAuthorizationContext authorization, IChannelManager channels, ArtworkAccess artwork, ContentAccess content,
    MusicLyrics lyrics, MusicMix mix, MediaBrowser.Controller.Dto.IDtoService dtos, AniListCatalog anime, SourceRegistry sites, OnlineSearch animeSearch, FilmSearch filmSearch,
    StreamingNavigation navigation, LibraryStreamingLinks links, FilmVersions films, Microsoft.Extensions.Caching.Memory.IMemoryCache jellyfinCache)
    : IAsyncActionFilter, IAsyncResultFilter
{
    private bool IsOurs(Guid id) => id != Guid.Empty && content.IsOurs(id);
    private bool IsOurChannel(Guid id) => content.IsChannel(id);
    private Task<bool> Allowed(ActionContext context) => artwork.AllowsAsync(context.HttpContext);
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Jellyfin's app opens a picked version as an item of its own. A StreamFin version is the title itself.
        if (context.HttpContext.Request.Method == "GET" && context.ActionArguments.TryGetValue("itemId", out var picked) && picked is Guid version
            && FilmVersions.TitleOf(version) is { } title)
            context.ActionArguments["itemId"] = title;
        // Jellyfin keeps a channel title's versions for five minutes and plays a picked one from that copy, which may still
        // hold only a guess at its streams. Pressing play on a Movies & TV title drops that copy, so StreamFin is asked
        // and probes the picked version first. Jellyfin keys the copy by the title's channel id.
        if (context.ActionDescriptor is ControllerActionDescriptor { ControllerName: "MediaInfo" } && context.ActionArguments.TryGetValue("itemId", out var played)
            && played is Guid playedId && IsOurs(playedId) && library.GetItemById(playedId)?.ExternalId is { } channelId && FilmVersions.Parse(channelId) is not null)
        {
            jellyfinCache.Remove(channelId);
            // Some apps name the picked version in the request body rather than the address.
            var chosen = context.HttpContext.Request.Query["mediaSourceId"].ToString();
            if (chosen.Length == 0)
                chosen = context.ActionArguments.Values.Where(v => v is not null && v is not string && !v.GetType().IsPrimitive)
                    .Select(v => v!.GetType().GetProperty("MediaSourceId")?.GetValue(v) as string).FirstOrDefault(s => s is { Length: > 0 }) ?? "";
            context.HttpContext.Items[FilmVersions.PickedKey] = chosen;
        }
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

    // A Movies & TV title's page lists every source's version, so the app's version picker can offer them. Jellyfin's own
    // copy only ever holds one. Versions found earlier show at once; otherwise the page waits a few seconds at most.
    private async Task VersionsAsync(BaseItemDto dto, HttpContext http)
    {
        if (dto.Type is not (BaseItemKind.Movie or BaseItemKind.Episode) || dto.MediaSources is null || !IsOurs(dto.Id)
            || library.GetItemById(dto.Id) is not { } item || FilmVersions.Parse(item.ExternalId) is null) return;
        var auth = await authorization.GetAuthorizationInfo(http);
        if (auth.User is null || !Plugin.Allows(auth.UserId)) return;
        if (await films.ForPageAsync(auth.UserId, item.ExternalId!, TimeSpan.FromSeconds(5), http.RequestAborted) is { Count: > 1 } versions)
        {
            dto.MediaSources = versions.ToArray();
            FilmVersions.Remember(versions.Select(v => v.Id), dto.Id);
        }
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
                    else { Countable(shown); await VersionsAsync(shown, context.HttpContext); result.Value = shown; }
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
