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
}
