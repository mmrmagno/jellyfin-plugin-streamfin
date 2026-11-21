using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;

namespace StreamFin;

[ApiController]
[Route("StreamFin/Internal/Stream")]
public sealed class StreamController(StreamRelay relay) : ControllerBase
{
    // FFmpeg cannot carry the user's Jellyfin session. These routes are loopback-only and additionally
    // require a random per-process header, an opaque ticket, and a still-authorized owner.
    [AllowAnonymous, HttpGet("{ticket}"), HttpHead("{ticket}")]
    public async Task Stream(string ticket)
    {
        // Jellyfin's probe hangs up on purpose once it has read enough; that is not an error worth a log line.
        try { await relay.ServeFileAsync(HttpContext, ticket); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { }
    }

    [AllowAnonymous, HttpGet("{ticket}/master.m3u8"), HttpHead("{ticket}/master.m3u8")]
    public Task Master(string ticket) => relay.ServeMasterAsync(HttpContext, ticket);

    [AllowAnonymous, HttpGet("{ticket}/p/{index:int}.{ext:length(2,6)}"), HttpHead("{ticket}/p/{index:int}.{ext:length(2,6)}")]
    public Task Part(string ticket, int index) => relay.ServePartAsync(HttpContext, ticket, index);

    // Fetched by Jellyfin's subtitle encoder, which sends its own User-Agent; the per-ticket key replaces the header.
    [AllowAnonymous, HttpGet("{ticket}/sub/{index:int}.vtt")]
    public Task Subtitle(string ticket, int index, [FromQuery] string key) => relay.ServeSubtitleAsync(HttpContext, ticket, index, key ?? "");
}
