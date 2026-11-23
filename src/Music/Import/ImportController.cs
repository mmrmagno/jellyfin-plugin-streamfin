using System.Security.Cryptography;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace StreamFin;

public sealed record ImportRequest(string? Text, string? Name);
public sealed record ImportExportRequest(string? Text);

// Extra locks on the import calls, beyond Jellyfin's sign-in check:
// - the token only in the Authorization header: one in the address (api_key=) ends up in browser history and logs;
// - a browser call another site started is refused (browsers mark it with Sec-Fetch-Site);
// - answers hold a user's playlists, so nothing keeps a copy.
public sealed class ImportGuardAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var request = context.HttpContext.Request;
        if (request.Query.Keys.Any(k => k.Equals("api_key", StringComparison.OrdinalIgnoreCase) || k.Equals("ApiKey", StringComparison.OrdinalIgnoreCase)))
            context.Result = new BadRequestObjectResult(new { Message = "Send the token in the Authorization header, not in the address." });
        else if (request.Headers["Sec-Fetch-Site"].ToString() is "cross-site" or "same-site")
            context.Result = new ObjectResult(new { Message = "Imports can only be started from this server's own page." }) { StatusCode = StatusCodes.Status403Forbidden };
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = "no-store";
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }
}

// The playlist import page and its calls. Unlike the settings, any user StreamFin is turned on for may use it; each
// user only ever sees and changes their own playlists. The page itself is public HTML; every call checks the user.
[ApiController, ImportGuard]
[Route("StreamFin/Import")]
public sealed class ImportController(PlaylistImport importer, MusicLibraryStore store, IAuthorizationContext authorization) : ControllerBase
{
    private const int TextLimit = 1_000_000;

    private async Task<Guid?> UserAsync()
    {
        var auth = await authorization.GetAuthorizationInfo(HttpContext);
        return auth.IsAuthenticated && auth.User is not null && Plugin.Allows(auth.UserId) ? auth.UserId : null;
    }

    private ActionResult Problem(ProviderException ex) => BadRequest(new { Message = ex.Message });

    // The page is plain HTML with no data in it, like Jellyfin's own web app: a browser opening an address sends no
    // token, so the page reads the signed-in user's token itself and every call it makes is checked. While import is
    // switched off it does not exist at all.
    [AllowAnonymous, HttpGet("")]
    public ActionResult Page()
    {
        if (!PlaylistImport.Enabled) return NotFound();
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Not framed, not opened into by other sites' windows, not embedded, no device access, not indexed.
        Response.Headers["X-Frame-Options"] = "DENY";
        Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
        Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
        Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), usb=(), payment=(), clipboard-read=()";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        return Content(ImportPage.Html.Replace("NONCE", nonce, StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    [Authorize, HttpPost("Start"), RequestSizeLimit(4 * TextLimit)]
    public async Task<ActionResult> Start([FromBody] ImportRequest request)
    {
        if (await UserAsync() is not { } user) return Forbid();
        if (!PlaylistImport.Enabled) return Problem(new ProviderException("Playlist import is switched off on this server."));
        if ((request.Text?.Length ?? 0) > TextLimit) return Problem(new ProviderException("That is too much text. Import at most 2000 songs at a time."));
        try { return Ok(new { importer.Start(user, request.Text, request.Name).Id }); }
        catch (ProviderException ex) { return Problem(ex); }
    }

    // A Spotify data export (Playlist1.json or YourLibrary.json), read in the browser and sent as text.
    [Authorize, HttpPost("Export"), RequestSizeLimit(16_000_000)]
    public async Task<ActionResult> Export([FromBody] ImportExportRequest request)
    {
        if (await UserAsync() is not { } user) return Forbid();
        if (!PlaylistImport.Enabled) return Problem(new ProviderException("Playlist import is switched off on this server."));
        try { return Ok(importer.ReadExport(user, request.Text ?? "").Select((list, index) => new { Index = index, list.Name, Songs = list.Tracks.Count })); }
        catch (ProviderException ex) { return Problem(ex); }
        catch (System.Text.Json.JsonException) { return Problem(new ProviderException("This file is not valid JSON. Use Playlist1.json or YourLibrary.json from Spotify's export.")); }
    }

    [Authorize, HttpPost("Export/{index:int}")]
    public async Task<ActionResult> ExportList(int index)
    {
        if (await UserAsync() is not { } user) return Forbid();
        if (!PlaylistImport.Enabled) return Problem(new ProviderException("Playlist import is switched off on this server."));
        try { return Ok(new { importer.StartExport(user, index).Id }); }
        catch (ProviderException ex) { return Problem(ex); }
    }

    [Authorize, HttpGet("Jobs/{id}")]
    public async Task<ActionResult> Job(string id)
    {
        if (await UserAsync() is not { } user) return Forbid();
        if (PlaylistImport.Job(user, id) is not { } job) return NotFound();
        return Ok(new { job.Name, job.State, job.Total, job.Done, job.Found, job.Message, job.Playlist, NotFound = job.NotFound.ToArray() });
    }

    [Authorize, HttpGet("Playlists")]
    public async Task<ActionResult> Playlists(CancellationToken ct)
    {
        if (await UserAsync() is not { } user) return Forbid();
        var library = await store.ReadAsync(user, ct);
        return Ok(new
        {
            Enabled = PlaylistImport.Enabled, SpotifyLinks = PlaylistImport.SpotifyLinks, YouTube = YtDlp.Wanted,
            Playlists = library.Playlists.Select(p => new { p.Id, p.Name, Songs = p.Entries.Count, p.Comment, p.Changed, Reimport = ImportReaders.FromSource(p.Source) is not null })
        });
    }

    [Authorize, HttpPost("Playlists/{id:guid}/Reimport")]
    public async Task<ActionResult> Reimport(Guid id, CancellationToken ct)
    {
        if (await UserAsync() is not { } user) return Forbid();
        if (!PlaylistImport.Enabled) return Problem(new ProviderException("Playlist import is switched off on this server."));
        try { return Ok(new { (await importer.ReimportAsync(user, id, ct)).Id }); }
        catch (ProviderException ex) { return Problem(ex); }
    }
}
