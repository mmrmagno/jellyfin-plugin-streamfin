using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AniFin;

// IChannel.IsEnabledFor hides the channel, but cached descendants can also be requested
// via Jellyfin's generic item/playback endpoints. Protect those routes and their results.
public sealed class ChannelAccessFilter(ILibraryManager library, IAuthorizationContext authorization, IChannelManager channels, ArtworkAccess artwork)
    : IAsyncActionFilter, IAsyncResultFilter
{
    // Jellyfin writes a channel's cache before persisting the returned children.
    // Serialize our native-view population so parallel web requests cannot observe that gap.
    private static readonly SemaphoreSlim NavigationLock = new(1, 1);
    private Guid ChannelId => library.GetNewItemId("Channel Anime", typeof(Channel));
    private bool IsOurs(Guid id)
    {
        if (id == Guid.Empty) return false;
        if (id == ChannelId) return true;
        return library.GetItemById(id)?.ChannelId == ChannelId;
    }
    private Task<bool> Allowed(ActionContext context) => artwork.AllowsAsync(context.HttpContext);
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
        if (!await Allowed(context))
        {
            if (ids.Any(IsOurs)) { context.Result = new NotFoundResult(); return; }
        }
        else
        {
            // Native series/season screens use the Shows API instead of Channel/Items.
            // Populate those children lazily before Jellyfin answers the normal request.
            var path = context.HttpContext.Request.Path.Value ?? "";
            if (context.HttpContext.Request.Method == "GET" && !path.Contains("/Images/", StringComparison.OrdinalIgnoreCase)
                && (path.StartsWith("/Shows/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase)))
            {
                var auth = await authorization.GetAuthorizationInfo(context.HttpContext);
                foreach (var id in ids)
                {
                    var item = library.GetItemById(id);
                    if (item is not Folder || (item.ChannelId != ChannelId && item.Id != ChannelId)) continue;
                    await NavigationLock.WaitAsync(context.HttpContext.RequestAborted);
                    try
                    {
                        await channels.GetChannelItemsInternal(new InternalItemsQuery(auth.User) { Parent = item, ChannelIds = [ChannelId] },
                            new Progress<double>(), context.HttpContext.RequestAborted);
                    }
                    finally { NavigationLock.Release(); }
                }
            }
        }
        await next();
    }
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!await Allowed(context) && context.Result is ObjectResult result)
        {
            switch (result.Value)
            {
                case BaseItemDto item when IsOurs(item.Id): context.Result = new NotFoundResult(); break;
                case QueryResult<BaseItemDto> query:
                    var filtered = query.Items.Where(i => !IsOurs(i.Id)).ToArray();
                    query.TotalRecordCount = Math.Max(filtered.Length, query.TotalRecordCount - (query.Items.Count - filtered.Length));
                    query.Items = filtered;
                    break;
                case IEnumerable<BaseItemDto> items: result.Value = items.Where(i => !IsOurs(i.Id)).ToArray(); break;
            }
        }
        await next();
    }
}
