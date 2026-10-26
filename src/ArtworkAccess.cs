using System.Text.RegularExpressions;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace StreamFin;

// Artwork is served to anyone holding the item ID, as Jellyfin does for library items:
// Android TV, Chromecast and other native clients load images without any credentials.
// The posters are public AniList/TMDB images and item IDs are random. Listings, metadata
// and playback still require a permitted user, and the content filter still applies.
public sealed class ArtworkAccess(IAuthorizationContext authorization)
{
    public static bool IsImage(HttpContext context) => (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        && Regex.IsMatch(context.Request.Path.Value ?? "", @"^/Items/[a-fA-F0-9-]+/Images/", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public async Task<bool> AllowsAsync(HttpContext context)
    {
        if (IsImage(context)) return true;
        var auth = await authorization.GetAuthorizationInfo(context);
        return auth.IsAuthenticated && Plugin.Allows(auth.UserId);
    }
}
