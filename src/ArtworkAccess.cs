using System.Security.Cryptography;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace AniFin;

// Jellyfin Web requests cover images without an Authorization header. An encrypted,
// HttpOnly, path-scoped cookie carries the existing session for artwork requests only.
// The token is revalidated by Jellyfin, so logout and account restrictions still apply.
public sealed class ArtworkAccess(IDataProtectionProvider protection, IAuthorizationContext authorization)
{
    private const string Cookie = "AniFinArtwork";
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("AniFin.Artwork.v1").ToTimeLimitedDataProtector();

    public async Task<bool> AllowsAsync(HttpContext context)
    {
        var auth = await authorization.GetAuthorizationInfo(context);
        if (auth.IsAuthenticated)
        {
            if (!Plugin.Allows(auth.UserId)) return false;
            if (auth.Token is { } token && !context.Response.HasStarted)
            {
                var existing = Read(context);
                if (existing != token)
                    context.Response.Cookies.Append(Cookie, protector.Protect(token, TimeSpan.FromHours(1)), new CookieOptions
                    {
                        HttpOnly = true, Secure = context.Request.IsHttps, SameSite = SameSiteMode.Strict,
                        Path = context.Request.PathBase + "/Items", MaxAge = TimeSpan.FromHours(1), IsEssential = true
                    });
            }
            return true;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(context.Request.Path.Value ?? "", @"^/Items/[a-fA-F0-9-]+/Images/", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;
        var session = Read(context);
        if (session is null) return false;
        var imageContext = new DefaultHttpContext();
        imageContext.Request.Headers.Authorization = "MediaBrowser Token=\"" + session + "\"";
        var imageAuth = await authorization.GetAuthorizationInfo(imageContext.Request);
        return imageAuth.IsAuthenticated && Plugin.Allows(imageAuth.UserId);
    }
    private string? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(Cookie, out var value)) return null;
        try { return protector.Unprotect(value, out _); }
        catch (CryptographicException) { return null; }
    }
}
