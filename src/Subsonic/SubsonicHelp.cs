using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

public sealed class NoFormBindingAttribute : Attribute, Microsoft.AspNetCore.Mvc.Filters.IResourceFilter
{
    public void OnResourceExecuting(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        for (var i = factories.Count - 1; i >= 0; i--)
            if (factories[i] is Microsoft.AspNetCore.Mvc.ModelBinding.FormValueProviderFactory or Microsoft.AspNetCore.Mvc.ModelBinding.FormFileValueProviderFactory
                or Microsoft.AspNetCore.Mvc.ModelBinding.JQueryFormValueProviderFactory) factories.RemoveAt(i);
    }

    public void OnResourceExecuted(Microsoft.AspNetCore.Mvc.Filters.ResourceExecutedContext context) { }
}

// People open the Subsonic address in a browser to check it; a bare 404 reads as "broken".
[ApiController]
[Route("StreamFin")]
public sealed class SubsonicHelpController : ControllerBase
{
    [AllowAnonymous, HttpGet(""), HttpGet("rest")]
    public ContentResult Help()
    {
        var address = System.Net.WebUtility.HtmlEncode($"{Request.Scheme}://{Request.Host}{Request.PathBase}/StreamFin");
        var state = SubsonicAuth.Enabled ? "The Subsonic API is on."
            : "The Subsonic API is off. An administrator can turn it on under Dashboard, Plugins, StreamFin, Subsonic apps.";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        return Content($$$"""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>StreamFin music apps</title>
            <style>body{font:16px/1.5 system-ui,sans-serif;max-width:36rem;margin:3rem auto;padding:0 1rem;color:#222;background:#fff}
            code{background:#eee;padding:.1rem .3rem;border-radius:.2rem;word-break:break-all}
            @media (prefers-color-scheme:dark){body{color:#ddd;background:#111}code{background:#333}}</style></head><body>
            <h1>StreamFin music apps</h1>
            <p>This address is for Subsonic music apps such as Symfonium, Feishin, Tempo or play:Sub, not for a browser.</p>
            <p>In the app, add a Subsonic or OpenSubsonic server with:</p>
            <ul><li>Server: <code>{{{address}}}</code></li><li>Username: your Jellyfin username</li>
            <li>Password: the app password from the StreamFin settings, not your Jellyfin password</li></ul>
            <p>{{{state}}}</p></body></html>
            """, "text/html; charset=utf-8");
    }
}
