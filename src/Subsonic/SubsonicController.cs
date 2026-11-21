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

// OpenSubsonic at <server>/StreamFin/rest/<method>[.view], for Symfonium, Feishin, Tempo, play:Sub and the like.
// Jellyfin's own sign-in does not apply here; every request carries Subsonic credentials checked by SubsonicAuth.
// IDs: ar-<artist>, al-<release>, tr-<release>-<track>, pl-<playlist>. A track needs its release, since the
// catalog only describes tracks inside releases.
[ApiController]
[Route("StreamFin/rest")]
public sealed partial class SubsonicController(SubsonicAuth auth, MusicCatalog catalog, MusicAudio sources, MusicPrefetch prefetch, MusicLibraryStore store, MusicLyrics lyrics, SafeHttp http, MusicCache songs,
    MusicSaver saver, MusicRadio radio, MusicArtCache covers, IMediaEncoder encoder, MediaBrowser.Controller.Drawing.IImageEncoder images,
    ILogger<SubsonicController> logger) : ControllerBase
{
    private const long FormLimit = 256 * 1024;
    // A song's FLAC header and size, kept from its first play so a later seek costs the catalog one request.
    private sealed record FlacHead(byte[] Header, long Size);
    private static readonly MemoryCache FlacHeads = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });

    // The first bytes of a song from its start: its ReplayGain, and the header a seek will need.
    private static void KeepHead(Uri upstream, byte[] bytes, long? size, string[] ids)
    {
        MusicGain.Remember(bytes, ids);
        if (size is > 0 && MusicGain.AudioStart(bytes) is int audio && audio < bytes.Length)
            FlacHeads.Set(upstream.AbsoluteUri, new FlacHead(bytes[..audio], size.Value), new MemoryCacheEntryOptions
            {
                Size = audio, SlidingExpiration = TimeSpan.FromHours(6)
            });
    }
    private static readonly SemaphoreSlim Transcodes = new(8, 8);
    // So one user (or one slow client) cannot hold every slot. iOS players open several ranges of the song that
    // is playing and preload the next one, so a single listener needs a few at once.
    private const int UserTranscodeLimit = 4;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> UserTranscodes = new();
    private SubsonicUser user = null!;
    private Dictionary<string, string[]> parameters = [];

    private string? One(string name) => parameters.TryGetValue(name, out var values) && values.Length > 0 ? values[0] : null;
    private string[] All(string name) => parameters.TryGetValue(name, out var values) ? values : [];
    private int Int(string name, int fallback, int min, int max) => int.TryParse(One(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : fallback;
    private bool Json => One("f") is "json" or "jsonp";

    public static string TrackId(MusicTrack t) => $"tr-{t.ReleaseId}-{t.Id}";
    public static (string Release, string Track)? ParseTrack(string? id) => id?.Split('-') is ["tr", var r, var t] && MusicCatalog.IsId(r) && MusicCatalog.IsId(t) ? (r, t) : null;
    public static string? ParseId(string? id, string prefix) => id is not null && id.StartsWith(prefix + "-", StringComparison.Ordinal) && MusicCatalog.IsId(id[(prefix.Length + 1)..]) ? id[(prefix.Length + 1)..] : null;
    public static Guid? ParsePlaylist(string? id) => id is not null && id.StartsWith("pl-", StringComparison.Ordinal) && Guid.TryParseExact(id[3..], "N", out var g) ? g : null;

    // ASP.NET stops at 1024 form values by default; a 2000 song playlist has more. Model binding would read
    // the form before Handle runs, so it is switched off and Handle reads it after its own checks.
    [AllowAnonymous, HttpGet("{method}"), HttpPost("{method}"), HttpHead("{method}"), NoFormBinding]
    [RequestFormLimits(ValueCountLimit = 5000, ValueLengthLimit = 16 * 1024, KeyLengthLimit = 256, MultipartBodyLengthLimit = FormLimit)]
    public async Task Handle(string method)
    {
        // Song details name the format a song is kept in, so the files from before a restart are read first.
        songs.Load();
        var name = method.EndsWith(".view", StringComparison.Ordinal) ? method[..^5] : method;
        // The method comes from the address; line breaks in it could fake extra log entries.
        var logged = (name.Length > 40 ? name[..40] : name).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        parameters = Request.Query.ToDictionary(q => q.Key, q => q.Value.OfType<string>().ToArray(), StringComparer.Ordinal);
        if (!SubsonicAuth.Enabled) { await SendAsync(SubsonicResponse.Error(50, "The Subsonic API is turned off in the StreamFin settings.")); return; }
        try
        {
            // OpenSubsonic: apps ask what the server supports before they sign in. It is the same fixed list for everyone.
            if (name == "getOpenSubsonicExtensions") { await SendAsync(Extensions()); return; }
            if (Request.HasFormContentType)
            {
                // Read before sign-in, so the size is checked first. A playlist of 2000 songs fits in well under this.
                if (Request.ContentLength is not (>= 0 and <= FormLimit)) { await SendAsync(SubsonicResponse.Error(0, "The request is too large.")); return; }
                IFormCollection form;
                try { form = await Request.ReadFormAsync(HttpContext.RequestAborted); }
                catch (InvalidDataException) { await SendAsync(SubsonicResponse.Error(0, "The request is too large.")); return; }
                foreach (var (key, value) in form)
                    parameters[key] = [.. All(key), .. value.OfType<string>()];
            }
            var (found, code, message) = await auth.CheckAsync(HttpContext.Connection.RemoteIpAddress, One("u"), One("t"), One("s"), One("p"), One("apiKey"), HttpContext.RequestAborted);
            if (found is null) { await SendAsync(SubsonicResponse.Error(code, message)); return; }
            user = found;
            if (await DispatchAsync(name) is { } node) await SendAsync(node);
        }
        // The app hung up (a skip, a seek, a closed screen); there is nobody left to answer.
        catch (Exception ex) when (ex is OperationCanceledException or IOException && HttpContext.RequestAborted.IsCancellationRequested) { }
        catch (SubsonicError error) when (!Response.HasStarted)
        {
            // A stream may have set its status, length and range already; the error replaces all of that.
            Response.Clear();
            await SendAsync(SubsonicResponse.Error(error.Code, error.Message));
        }
        catch (SubsonicError error)
        {
            logger.LogWarning("StreamFin Subsonic {Method} stopped: {Message}", logged, error.Message);
            HttpContext.Abort();
        }
        catch (BadHttpRequestException) when (!Response.HasStarted) { await SendAsync(SubsonicResponse.Error(0, "The request could not be read.")); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException or UriFormatException
            or IOException or OperationCanceledException)
        {
            if (HttpContext.RequestAborted.IsCancellationRequested) return;
            logger.LogWarning("StreamFin Subsonic {Method} failed ({ErrorType})", logged, ex.GetType().Name);
            // Headers may promise a length and a range already; the error replaces them. Once bytes have gone out, the
            // only honest answer is to cut the connection: a short body under a full length would read as the whole song.
            if (Response.HasStarted) { HttpContext.Abort(); return; }
            Response.Clear();
            await SendAsync(SubsonicResponse.Error(0, ex is ProviderException and not SourceStatusException ? ex.Message
                : ex is IOException ? "The music catalog stopped sending this song. Please try again."
                : "The music catalog is not responding right now. Please try again in a few minutes."));
        }
    }

    private sealed class SubsonicError(int code, string message) : Exception(message) { public int Code { get; } = code; }
    private static SubsonicError Missing(string name) => new(10, "Required parameter is missing: " + name);
    private static SubsonicError NotFound(string what) => new(70, what + " not found.");

    // JSONP wraps the JSON in a call to the named function. The name is limited to a plain identifier path, so it
    // cannot carry script of its own.
    private string? Callback => One("f") == "jsonp" && One("callback") is { } name
        && Regex.IsMatch(name, @"\A[A-Za-z_$][A-Za-z0-9_$]{0,63}(?:\.[A-Za-z_$][A-Za-z0-9_$]{0,63}){0,3}\z", RegexOptions.None, TimeSpan.FromSeconds(1)) ? name : null;

    private async Task SendAsync(SubsonicNode node)
    {
        if (Response.HasStarted) return;
        Response.StatusCode = 200;
        var callback = Callback;
        Response.ContentType = callback is not null ? "application/javascript; charset=utf-8" : Json ? "application/json; charset=utf-8" : "text/xml; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var body = Json ? SubsonicResponse.Json(node) : SubsonicResponse.Xml(node);
        // The leading comment is the usual guard against a response being read as something other than script.
        if (callback is not null) body = [.. System.Text.Encoding.UTF8.GetBytes("/**/" + callback + "("), .. body, .. ");"u8];
        if (!HttpMethods.IsHead(Request.Method)) await Response.Body.WriteAsync(body, HttpContext.RequestAborted);
    }

    private static SubsonicNode Success() => SubsonicResponse.Root(true);
    private CancellationToken Ct => HttpContext.RequestAborted;

    private Task<SubsonicNode?> DispatchAsync(string method) => method switch
    {
        "ping" => Done(Success()),
        "getLicense" => Done(Success().One(new SubsonicNode("license").Set("valid", true))),
        "getOpenSubsonicExtensions" => Done(Extensions()),
        "tokenInfo" => Done(Success().One(new SubsonicNode("tokenInfo").Set("username", user.Name))),
        "getMusicFolders" => Done(Success().One(new SubsonicNode("musicFolders").Add(new SubsonicNode("musicFolder").Set("id", 1).Set("name", "StreamFin")))),
        "getUser" => Done(One("username") is { } asked && !string.Equals(asked, user.Name, StringComparison.OrdinalIgnoreCase)
            ? SubsonicResponse.Error(50, "Only your own account can be looked up.") : Success().One(UserNode())),
        "getScanStatus" or "startScan" => Done(Success().One(new SubsonicNode("scanStatus").Set("scanning", false).Set("count", 0))),
        "getGenres" => Done(Success().One(new SubsonicNode("genres"))),
        "getSongsByGenre" => Done(Success().One(new SubsonicNode("songsByGenre"))),
        "getNowPlaying" => NowPlayingAsync(),
        "getBookmarks" => BookmarksAsync(),
        "createBookmark" => CreateBookmarkAsync(),
        "deleteBookmark" => DeleteBookmarkAsync(),
        "getPlayQueue" => PlayQueueAsync(),
        "savePlayQueue" => SavePlayQueueAsync(),
        "setRating" => RatingAsync(),
        "getInternetRadioStations" => Done(Success().One(new SubsonicNode("internetRadioStations"))),
        "getPodcasts" => Done(Success().One(new SubsonicNode("podcasts"))),
        "getNewestPodcasts" => Done(Success().One(new SubsonicNode("newestPodcasts"))),
        "getShares" => Done(Success().One(new SubsonicNode("shares"))),
        "getArtists" => ArtistsAsync("artists"),
        "getIndexes" => ArtistsAsync("indexes"),
        "getArtist" => ArtistAsync(),
        "getAlbum" => AlbumAsync(),
        "getSong" => SongAsync(),
        "getMusicDirectory" => DirectoryAsync(),
        "getArtistInfo" or "getArtistInfo2" => ArtistInfoAsync(method == "getArtistInfo" ? "artistInfo" : "artistInfo2"),
        "getAlbumInfo" or "getAlbumInfo2" => Done(Success().One(new SubsonicNode(method == "getAlbumInfo" ? "albumInfo" : "albumInfo").Set("notes", ""))),
        "getTopSongs" => TopSongsAsync(),
        "getSimilarSongs" or "getSimilarSongs2" => SimilarAsync(method == "getSimilarSongs" ? "similarSongs" : "similarSongs2"),
        "search2" or "search3" => SearchAsync(method == "search2" ? "searchResult2" : "searchResult3"),
        "getAlbumList" or "getAlbumList2" => AlbumListAsync(method == "getAlbumList" ? "albumList" : "albumList2"),
        "getRandomSongs" => RandomSongsAsync(),
        "getStarred" or "getStarred2" => StarredAsync(method == "getStarred" ? "starred" : "starred2"),
        "star" => StarAsync(true),
        "unstar" => StarAsync(false),
        "scrobble" => ScrobbleAsync(),
        "getPlaylists" => PlaylistsAsync(),
        "getPlaylist" => PlaylistAsync(),
        "createPlaylist" => CreatePlaylistAsync(),
        "updatePlaylist" => UpdatePlaylistAsync(),
        "deletePlaylist" => DeletePlaylistAsync(),
        "getLyricsBySongId" => LyricsBySongAsync(),
        "getLyrics" => LyricsAsync(),
        "getCoverArt" => CoverAsync(),
        "stream" or "download" => StreamAsync(method == "download"),
        _ => Done(SubsonicResponse.Error(0, "Unknown or unsupported method: " + method))
    };

    private static Task<SubsonicNode?> Done(SubsonicNode node) => Task.FromResult<SubsonicNode?>(node);

    private static SubsonicNode Extensions() => Success().AddRange(new[] { "formPost", "apiKeyAuthentication", "songLyrics", "transcodeOffset" }
        .Select(e => new SubsonicNode("openSubsonicExtensions").Set("name", e).Set("versions", new[] { 1 })));

    private SubsonicNode UserNode() => new SubsonicNode("user").Set("username", user.Name).Set("scrobblingEnabled", true).Set("adminRole", false)
        .Set("settingsRole", false).Set("downloadRole", true).Set("uploadRole", false).Set("playlistRole", true).Set("coverArtRole", true)
        .Set("commentRole", false).Set("podcastRole", false).Set("streamRole", true).Set("jukeboxRole", false).Set("shareRole", false)
        .Set("videoConversionRole", false).Set("folder", new[] { 1 });
}
