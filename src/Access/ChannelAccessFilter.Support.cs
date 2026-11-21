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

    private static T? Argument<T>(ActionExecutingContext context, string name) =>
        context.ActionArguments.FirstOrDefault(a => a.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is T value ? value : default;
}
