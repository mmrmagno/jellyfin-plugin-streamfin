using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

public sealed class StreamingLibraryLink
{
    public string LibraryId { get; set; } = "";
    public string Category { get; set; } = "";
}

// A StreamFin entry at the top of a chosen Jellyfin library. It is added to the listing only, never saved into the
// library, so the library's own items and scans are untouched. Adapted from ItzDabbzz's fork (GPL-2.0).
public sealed class LibraryStreamingLinks(ILibraryManager library, StreamingNavigation navigation)
{
    public static readonly string[] Categories = ["anime", "movies", "tv", "music"];
    private static readonly object StateKey = new();
    private sealed record Page(int Start, int? Limit, BaseItemDto Link);

    // The link takes the first row of the first page; every page of the library's own items moves down by one.
    public static (int Start, int? Limit) NativePage(int start, int? limit) =>
        (Math.Max(0, start - 1), start == 0 && limit.HasValue ? Math.Max(0, limit.Value - 1) : limit);

    public async Task PrepareAsync(ActionExecutingContext context, User user, CancellationToken ct)
    {
        if (!Plugin.Allows(user.Id) || context.HttpContext.Request.Method != "GET"
            || context.ActionDescriptor is not ControllerActionDescriptor { ActionName: "GetItems" or "GetItemsByUserIdLegacy" }) return;
        string Arg(string name)
        {
            var value = context.ActionArguments.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
            return value is System.Collections.IEnumerable list and not string
                ? string.Join(',', list.Cast<object>())
                : value?.ToString() ?? context.HttpContext.Request.Query.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.ToString();
        }
        if (Guid.TryParse(Arg("userId"), out var asked) && asked != Guid.Empty && asked != user.Id) return;
        if (!string.IsNullOrWhiteSpace(Arg("searchTerm")) || string.Equals(Arg("isFolder"), "false", StringComparison.OrdinalIgnoreCase)) return;
        // The library's grid can show the link; Play all and queue requests (MediaSources, filters, other kinds) cannot.
        if (Arg("includeItemTypes").Split(',', StringSplitOptions.RemoveEmptyEntries).Any(type => type is not ("Movie" or "Series" or "MusicAlbum" or "MusicArtist" or "Audio"))) return;
        if (Arg("fields").Split(',').Contains("MediaSources", StringComparer.OrdinalIgnoreCase)) return;
        foreach (var key in new[] { "excludeItemTypes", "mediaTypes", "filters", "genres", "genreIds", "years", "person", "personIds", "studioIds", "tags",
            "isPlayed", "isFavorite", "ids", "hasSubtitles", "isMissing", "minCommunityRating", "nameStartsWith", "nameLessThan" })
            if (!string.IsNullOrWhiteSpace(Arg(key))) return;
        if (!Guid.TryParse(Arg("parentId"), out var parentId) || parentId == Guid.Empty
            || library.GetItemById(parentId) is not CollectionFolder folder || !folder.IsVisible(user)) return;
        var mapping = (Plugin.Instance?.Configuration.StreamingLibraryLinks ?? []).FirstOrDefault(link => Guid.TryParse(link.LibraryId, out var id) && id == parentId);
        if (mapping is null) return;
        var start = int.TryParse(Arg("startIndex"), out var askedStart) ? Math.Max(0, askedStart) : 0;
        int? limit = int.TryParse(Arg("limit"), out var askedLimit) ? askedLimit : null;
        if (limit <= 0) return;
        // Resolved before a row is reserved, so a category that is off leaves no gap.
        var link = await navigation.ResolveTargetAsync(mapping.Category, user, ct);
        if (link is null) return;
        link.Name = "StreamFin";
        context.HttpContext.Items[StateKey] = new Page(start, limit, link);
        var native = NativePage(start, limit);
        void Set(string name, object? value) =>
            context.ActionArguments[context.ActionArguments.Keys.FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name] = value;
        Set("startIndex", native.Start);
        // Jellyfin still answers a page that only holds the link, for its counts; one row is fetched and dropped below.
        Set("limit", native.Limit == 0 ? 1 : native.Limit);
    }

    public static void Apply(QueryResult<BaseItemDto> result, Microsoft.AspNetCore.Http.HttpContext context)
    {
        if (context.Items[StateKey] is not Page page) return;
        if (page.Start == 0) result.Items = new[] { page.Link }.Concat(page.Limit == 1 ? [] : result.Items).ToArray();
        result.StartIndex = page.Start;
        result.TotalRecordCount++;
    }
}
