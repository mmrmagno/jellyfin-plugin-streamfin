using Jellyfin.Data.Enums;

namespace StreamFin;

// Pure rules for searches made on a catalog page (see ChannelAccessFilter.CatalogSearchAsync).
public static class CatalogSearch
{
    public static bool IsBrowseAction(string? action) => action is "GetItems" or "GetItemsByUserIdLegacy" or "GetChannelItems";

    public static bool IsCatalog(string? id) => id?.Split('/') is ["catalog", "popular" or "season" or "az", _]
        or ["list", "movies" or "tv" or "trending", _];

    // Anime is all series. Movies & TV is movies and series. A row that excludes a kind (a Videos row excludes Movie,
    // a Programs row includes only live TV) gets none of ours for that kind.
    public static BaseItemKind[] Kinds(bool isAnime, BaseItemKind[] include, BaseItemKind[] exclude)
    {
        BaseItemKind[] requested = include.Length > 0 ? include : exclude.Length == 0 ? [BaseItemKind.Movie, BaseItemKind.Series] : [];
        return requested.Where(kind => (isAnime ? kind == BaseItemKind.Series : kind is BaseItemKind.Movie or BaseItemKind.Series)
            && !exclude.Contains(kind)).ToArray();
    }
}
