using System.Text.RegularExpressions;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;

namespace StreamFin;

// Artwork is served without credentials, as Jellyfin does for library items: Android TV,
// Chromecast and other native clients load images that way. Item IDs are derived from public
// AniList/TMDB IDs, so anyone who can reach the server can tell whether a title has been
// browsed or searched for here. Listings, metadata and playback still need a permitted user.
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
