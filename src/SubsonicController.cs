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
public sealed class SubsonicController(SubsonicAuth auth, MusicCatalog catalog, MusicLibraryStore store, MusicLyrics lyrics, SafeHttp http, MusicCache songs,
    MusicSaver saver, MusicRadio radio, MusicArtCache covers, IMediaEncoder encoder, ILogger<SubsonicController> logger) : ControllerBase
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

    // ---- Library: what this user played, starred or put in a playlist. The catalog has no browsable index.

    private sealed record Played(int Count, DateTime Last);
    private sealed record Starred(Dictionary<string, DateTime> Tracks, Dictionary<string, DateTime> Albums, Dictionary<string, DateTime> Artists,
        Dictionary<string, int> Ratings, Dictionary<string, Played> TrackPlays, Dictionary<string, Played> AlbumPlays)
    {
        public int? Rating(string id) => Ratings.TryGetValue(id, out var rating) ? rating : null;
    }
    private static Starred Stars(MusicLibrary library) => new(
        library.StarredTracks.GroupBy(s => s.Track.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredAlbums.GroupBy(s => s.Release.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredArtists.GroupBy(s => s.Artist.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.Ratings,
        library.Plays.GroupBy(p => p.Track.Id).ToDictionary(g => g.Key, g => new Played(g.Count(), g.Max(p => p.At))),
        library.Plays.GroupBy(p => p.Track.ReleaseId).ToDictionary(g => g.Key, g => new Played(g.Count(), g.Max(p => p.At))));

    private static IEnumerable<MusicPlay> Songs(MusicLibrary library) => library.Plays.Concat(library.StarredTracks)
        .Concat(library.Playlists.SelectMany(p => p.Entries)).DistinctBy(p => p.Track.Id);

    private sealed record LibraryAlbum(MusicRelease Release, DateTime Added, DateTime? Played, int Plays);
    private static List<LibraryAlbum> Albums(MusicLibrary library)
    {
        var albums = new Dictionary<string, LibraryAlbum>();
        foreach (var play in library.Plays)
        {
            var t = play.Track;
            albums[t.ReleaseId] = albums.TryGetValue(t.ReleaseId, out var known)
                ? known with { Added = play.At < known.Added ? play.At : known.Added, Played = known.Played > play.At ? known.Played : play.At, Plays = known.Plays + 1 }
                : new LibraryAlbum(new MusicRelease(t.ReleaseId, play.Album, t.Artists, "ALBUM", null, null, t.Artwork, null, []), play.At, play.At, 1);
        }
        foreach (var star in library.StarredAlbums)
            albums[star.Release.Id] = albums.TryGetValue(star.Release.Id, out var known)
                ? known with { Release = star.Release, Added = star.At < known.Added ? star.At : known.Added }
                : new LibraryAlbum(star.Release, star.At, null, 0);
        foreach (var song in library.StarredTracks.Concat(library.Playlists.SelectMany(p => p.Entries)))
            albums.TryAdd(song.Track.ReleaseId, new LibraryAlbum(new MusicRelease(song.Track.ReleaseId, song.Album, song.Track.Artists, "ALBUM", null, null,
                song.Track.Artwork, null, []), song.At, null, 0));
        return [.. albums.Values];
    }

    // The library grows with taste: every album of an artist this user starred or keeps playing is listed too,
    // so apps that show "all albums" or sync the library are not limited to what was played. Only artist pages
    // the server already keeps are used; missing ones are fetched in the background while the catalog has room.
    private const int GrowthArtists = 50;
    private const int GrowthAlbums = 100;
    private const int GrowthPlays = 3;

    private static IEnumerable<string> GrowthArtistIds(MusicLibrary library) => library.StarredArtists.OrderByDescending(s => s.At).Select(s => s.Artist.Id)
        .Concat(library.Plays.Where(p => p.Track.Artists.Count > 0).GroupBy(p => p.Track.Artists[0].Id).Where(g => g.Count() >= GrowthPlays)
            .OrderByDescending(g => g.Count()).Select(g => g.Key))
        .Distinct().Take(GrowthArtists);

    // Built once per user and library version, and again every few minutes while missing artist pages arrive. Changed
    // moves only when the set of albums does, for apps that compare the index date.
    private sealed record Grown(long Version, IReadOnlyList<LibraryAlbum> Albums, DateTime Changed, DateTime Built);
    private static readonly MemoryCache Growth = new(new MemoryCacheOptions { SizeLimit = 1000 });

    private async Task<Grown> GrowthAsync(MusicLibrary library)
    {
        var version = store.Version(user.Id);
        Growth.TryGetValue<Grown>(user.Id, out var known);
        if (known is not null && known.Version == version && DateTime.UtcNow - known.Built < TimeSpan.FromMinutes(2)) return known;
        var listed = Albums(library).Select(a => a.Release.Id).ToHashSet();
        var grown = new List<LibraryAlbum>();
        var started = 0;
        foreach (var artistId in GrowthArtistIds(library))
        {
            if (!catalog.HasArtist(artistId))
            {
                if (started++ < 2 && catalog.Spare && !catalog.Down) _ = WarmArtistAsync(artistId);
                continue;
            }
            MusicArtistPage page;
            try { page = await catalog.ArtistAsync(artistId, Ct); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException or RegexMatchTimeoutException
                or OperationCanceledException && !Ct.IsCancellationRequested) { continue; }
            // The catalog lists a clean edit next to the explicit album; one entry per title and length is enough.
            foreach (var release in page.Releases.GroupBy(r => (r.Title.ToLowerInvariant(), r.Type, r.TrackCount)).Select(g => g.First()).Take(GrowthAlbums))
                if (listed.Add(release.Id))
                    grown.Add(new LibraryAlbum(release, release.Date ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, 0));
        }
        var changed = known is not null && known.Albums.Select(a => a.Release.Id).ToHashSet().SetEquals(grown.Select(a => a.Release.Id)) ? known.Changed : DateTime.UtcNow;
        var result = new Grown(version, grown, changed, DateTime.UtcNow);
        Growth.Set(user.Id, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(30) });
        return result;
    }

    private async Task<List<LibraryAlbum>> AlbumsAsync(MusicLibrary library)
    {
        var albums = Albums(library);
        var listed = albums.Select(a => a.Release.Id).ToHashSet();
        albums.AddRange((await GrowthAsync(library)).Albums.Where(a => listed.Add(a.Release.Id)));
        return albums;
    }

    // Albums per artist, counted once for a whole listing.
    private static Dictionary<string, int> AlbumCounts(List<LibraryAlbum> albums) => albums.SelectMany(al => al.Release.Artists.Select(a => a.Id).Distinct())
        .GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());

    private async Task WarmArtistAsync(string artistId)
    {
        try { await catalog.ArtistAsync(artistId, CancellationToken.None); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or OperationCanceledException or IOException
            or RegexMatchTimeoutException) { }
    }

    private static List<MusicArtistRef> Artists(MusicLibrary library) => library.StarredArtists.Select(s => s.Artist)
        .Concat(Songs(library).SelectMany(p => p.Track.Artists)).Concat(library.StarredAlbums.SelectMany(s => s.Release.Artists))
        .DistinctBy(a => a.Id).ToList();

    // ---- Nodes

    private static string Names(IEnumerable<MusicArtistRef> artists) => string.Join(", ", artists.Select(a => a.Name));
    private static string Kind(string type) => type switch { "SINGLE" => "Single", "EP" => "EP", "COMPILATION" => "Compilation", _ => "Album" };

    private static SubsonicNode Song(string element, MusicTrack t, string album, Starred stars, MusicRelease? release = null)
    {
        var node = new SubsonicNode(element).Set("id", TrackId(t)).Set("parent", "al-" + t.ReleaseId).Set("isDir", false).Set("title", t.Title)
            .Set("album", album).Set("artist", Names(t.Artists)).Set("track", t.Number).Set("discNumber", t.Disc).Set("coverArt", "al-" + t.ReleaseId)
            .Set("contentType", "audio/flac").Set("suffix", "flac").Set("duration", t.DurationMs is > 0 ? (int)Math.Round(t.DurationMs.Value / 1000.0) : null)
            .Set("path", $"{MusicPaths.Part(t.Artists.FirstOrDefault()?.Name ?? "Unknown")}/{MusicPaths.Part(album)}/{t.Number ?? 0:00} {MusicPaths.Part(t.Title)}.flac")
            .Set("albumId", "al-" + t.ReleaseId).Set("artistId", t.Artists.Count > 0 ? "ar-" + t.Artists[0].Id : null).Set("type", "music")
            .Set("mediaType", "song").Set("isVideo", false).Set("displayArtist", Names(t.Artists))
            .Set("isrc", t.Isrc is null ? null : new[] { t.Isrc }).Set("explicitStatus", t.Explicit ? "explicit" : "clean")
            .Set("starred", stars.Tracks.TryGetValue(t.Id, out var at) ? at : null)
            .Set("userRating", stars.Rating(TrackId(t))).Set("playCount", stars.TrackPlays.GetValueOrDefault(t.Id)?.Count)
            .Set("played", stars.TrackPlays.GetValueOrDefault(t.Id)?.Last).Set("year", release?.Date?.Year)
            .Set("displayAlbumArtist", release is { Artists.Count: > 0 } ? Names(release.Artists) : null);
        // Known once the song has passed through here: its header says how it was mastered.
        if (MusicGain.FormatFor(t.Id) is { } format)
            node.Set("samplingRate", format.SampleRate).Set("bitDepth", format.BitDepth).Set("channelCount", format.Channels);
        // Amperfy hides a song whose size is unknown, and the catalog JSON has none: the real one once the song
        // has played here, otherwise an estimate from the length (lossless averages about 70% of CD rate).
        var duration = t.DurationMs is > 0 ? t.DurationMs.Value / 1000.0 : 0;
        var known = MusicCache.Size(t.Id) ?? (FlacHeads.TryGetValue<FlacHead>(MusicCatalog.StreamUri(t.Id).AbsoluteUri, out var head) ? head?.Size : null);
        node.Set("size", known ?? (duration > 0 ? (long)(duration * 1411 * 125 * 0.7) : null))
            .Set("bitRate", known is long bytes && duration > 0 ? (int)Math.Round(bytes * 8 / duration / 1000) : null);
        if (MusicGain.For(t.Id) is { } gain)
            node.One(new SubsonicNode("replayGain").Set("trackGain", gain.TrackGain).Set("albumGain", gain.AlbumGain)
                .Set("trackPeak", gain.TrackPeak).Set("albumPeak", gain.AlbumPeak));
        node.AddRange(t.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
        return node.AddRange((release?.Artists ?? []).Select(a => new SubsonicNode("albumArtists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
    }

    private static SubsonicNode Album(string element, MusicRelease r, Starred stars, bool directory = false)
    {
        var songs = r.Tracks.Count > 0 ? r.Tracks.Count : r.TrackCount ?? 0;
        var node = new SubsonicNode(element).Set("id", "al-" + r.Id).Set("name", r.Title).Set("title", r.Title).Set("album", r.Title)
            .Set("artist", Names(r.Artists)).Set("artistId", r.Artists.Count > 0 ? "ar-" + r.Artists[0].Id : null).Set("coverArt", "al-" + r.Id)
            .Set("songCount", songs).Set("duration", (int)Math.Round(r.Tracks.Sum(t => t.DurationMs ?? 0) / 1000.0)).Set("year", r.Date?.Year)
            .Set("created", r.Date ?? new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Set("isDir", true)
            .Set("parent", directory && r.Artists.Count > 0 ? "ar-" + r.Artists[0].Id : null)
            .Set("starred", stars.Albums.TryGetValue(r.Id, out var at) ? at : null).Set("displayArtist", Names(r.Artists))
            .Set("releaseTypes", new[] { Kind(r.Type) }).Set("isCompilation", r.Type == "COMPILATION")
            .Set("explicitStatus", r.Tracks.Count == 0 ? null : r.Tracks.Any(t => t.Explicit) ? "explicit" : "clean")
            .Set("userRating", stars.Rating("al-" + r.Id)).Set("playCount", stars.AlbumPlays.GetValueOrDefault(r.Id)?.Count)
            .Set("played", stars.AlbumPlays.GetValueOrDefault(r.Id)?.Last).Set("sortName", SortName(r.Title));
        if (r.Date is { } date) node.One(new SubsonicNode("originalReleaseDate").Set("year", date.Year).Set("month", date.Month).Set("day", date.Day));
        if (r.Label is { Length: > 0 } label) node.Add(new SubsonicNode("recordLabels").Set("name", label));
        return node.AddRange(r.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
    }

    private static SubsonicNode Artist(string element, MusicArtistRef a, int albums, Starred stars) => new SubsonicNode(element).Set("id", "ar-" + a.Id)
        .Set("name", a.Name).Set("coverArt", "ar-" + a.Id).Set("albumCount", albums).Set("starred", stars.Artists.TryGetValue(a.Id, out var at) ? at : null)
        .Set("userRating", stars.Rating("ar-" + a.Id)).Set("sortName", SortName(a.Name));

    private async Task<(MusicLibrary Library, Starred Stars)> LibraryAsync()
    {
        var library = await store.ReadAsync(user.Id, Ct);
        return (library, Stars(library));
    }

    // The catalog budget is shared by everyone, so one request (a big playlist, a mass star) may only send
    // so many lookups to the catalog. What is already cached costs nothing.
    private const int FetchLimit = 40;
    private int fetches;
    private bool Fetch(bool cached) => cached || ++fetches <= FetchLimit
        ? true : throw new SubsonicError(0, "Too many albums at once for the music catalog. Try again in a minute: the ones already fetched stay cached.");

    // A song the catalog has answered for before comes from that copy. While the catalog cannot answer, one this
    // user played, starred or put in a playlist is described from their own library, so scrobbles, stars, lyrics
    // and playback carry on. Only a song neither knows has to wait for the catalog.
    private async Task<(MusicTrack Track, MusicRelease Release)> TrackAsync(string? id)
    {
        if (ParseTrack(id ?? throw Missing("id")) is not var (releaseId, trackId)) throw NotFound("Song");
        if (!catalog.HasRelease(releaseId) && catalog.Down && await KnownAsync(releaseId, trackId) is { } known) return known;
        Fetch(catalog.HasRelease(releaseId));
        MusicRelease release;
        try { release = await catalog.ReleaseAsync(releaseId, Ct); }
        catch (Exception ex) when (ex is ProviderException or JsonException)
        {
            if (await KnownAsync(releaseId, trackId) is { } fallback) return fallback;
            throw;
        }
        var track = release.Tracks.FirstOrDefault(t => t.Id == trackId) ?? throw NotFound("Song");
        return (track with { Artwork = track.Artwork ?? release.Artwork }, release);
    }

    private Dictionary<(string, string), MusicPlay>? knownSongs;
    private async Task<(MusicTrack Track, MusicRelease Release)?> KnownAsync(string releaseId, string trackId)
    {
        knownSongs ??= Songs(await store.ReadAsync(user.Id, Ct)).Where(p => p.Track.Title.Length > 0)
            .GroupBy(p => (p.Track.ReleaseId, p.Track.Id)).ToDictionary(g => g.Key, g => g.First());
        if (!knownSongs.TryGetValue((releaseId, trackId), out var play)) return null;
        return (play.Track, new MusicRelease(releaseId, play.Album, play.Track.Artists, "ALBUM", null, null, play.Track.Artwork, null, []));
    }

    private async Task<IReadOnlyList<MusicRelease>> Releases(MusicArtistPage artist) => MusicCatalog.PreferExplicit
        ? await catalog.WithoutCleanCopiesAsync(artist.Releases, Ct) : artist.Releases;

    // ---- Browsing

    private static readonly string[] Articles = ["The", "El", "La", "Los", "Las", "Le", "Les"];
    private static string SortName(string name)
    {
        foreach (var article in Articles)
            if (name.StartsWith(article + " ", StringComparison.OrdinalIgnoreCase) && name.Length > article.Length + 1) return name[(article.Length + 1)..];
        return name;
    }

    private async Task<SubsonicNode?> ArtistsAsync(string element)
    {
        var (library, stars) = await LibraryAsync();
        var grown = await GrowthAsync(library);
        var changed = new DateTimeOffset(library.Changed > grown.Changed ? library.Changed : grown.Changed).ToUnixTimeMilliseconds();
        var root = new SubsonicNode(element).Set("ignoredArticles", string.Join(' ', Articles)).Set("lastModified", element == "indexes" ? changed : null);
        // An app that already has this version of the index gets an empty answer, as the API allows.
        if (element == "indexes" && long.TryParse(One("ifModifiedSince"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var since) && since >= changed)
            return Success().One(root);
        var counts = AlbumCounts(await AlbumsAsync(library));
        foreach (var group in Artists(library).GroupBy(a => SortName(a.Name) is { Length: > 0 } s && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]).ToString() : "#")
                     .OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal))
            root.Add(new SubsonicNode("index").Set("name", group.Key).AddRange(group.OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
                .Select(a => Artist("artist", a, counts.GetValueOrDefault(a.Id), stars))));
        return Success().One(root);
    }

    private async Task<SubsonicNode?> ArtistAsync()
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "ar") ?? throw NotFound("Artist");
        var artist = await catalog.ArtistAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        var releases = await Releases(artist);
        return Success().One(Artist("artist", new MusicArtistRef(artist.Id, artist.Name), releases.Count, stars)
            .AddRange(releases.OrderByDescending(r => r.Date).Select(r => Album("album", r, stars))));
    }

    private async Task<SubsonicNode?> AlbumAsync()
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "al") ?? throw NotFound("Album");
        var release = await catalog.ReleaseAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        return Success().One(Album("album", release, stars).AddRange(release.Tracks.Select(t => Song("song", t with { Artwork = t.Artwork ?? release.Artwork }, release.Title, stars, release))));
    }

    private async Task<SubsonicNode?> SongAsync()
    {
        var (track, release) = await TrackAsync(One("id"));
        var (_, stars) = await LibraryAsync();
        return Success().One(Song("song", track, release.Title, stars, release.Tracks.Count > 0 ? release : null));
    }

    // Folder browsing for older clients: an artist holds albums, an album holds songs.
    private async Task<SubsonicNode?> DirectoryAsync()
    {
        var id = One("id") ?? throw Missing("id");
        var (_, stars) = await LibraryAsync();
        if (ParseId(id, "ar") is { } artistId)
        {
            var artist = await catalog.ArtistAsync(artistId, Ct);
            return Success().One(new SubsonicNode("directory").Set("id", id).Set("name", artist.Name)
                .AddRange((await Releases(artist)).OrderByDescending(r => r.Date).Select(r => Album("child", r, stars, directory: true))));
        }
        if (ParseId(id, "al") is { } releaseId)
        {
            var release = await catalog.ReleaseAsync(releaseId, Ct);
            return Success().One(new SubsonicNode("directory").Set("id", id).Set("name", release.Title)
                .Set("parent", release.Artists.Count > 0 ? "ar-" + release.Artists[0].Id : null)
                .AddRange(release.Tracks.Select(t => Song("child", t, release.Title, stars, release))));
        }
        if (id == "1")
        {
            var (library, _) = await LibraryAsync();
            return Success().One(new SubsonicNode("directory").Set("id", "1").Set("name", "StreamFin")
                .AddRange(Artists(library).OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
                    .Select(a => new SubsonicNode("child").Set("id", "ar-" + a.Id).Set("title", a.Name).Set("isDir", true).Set("coverArt", "ar-" + a.Id))));
        }
        throw NotFound("Directory");
    }

    private async Task<SubsonicNode?> ArtistInfoAsync(string element)
    {
        var id = ParseId(One("id") ?? throw Missing("id"), "ar") ?? throw NotFound("Artist");
        var count = Int("count", 20, 0, 50);
        var artist = await catalog.ArtistAsync(id, Ct);
        var (_, stars) = await LibraryAsync();
        // Apps load this with the artist page: what is known in a few seconds now, the rest from the cache next time.
        var similar = count == 0 ? [] : await radio.SimilarArtistsAsync(artist, Ct, DateTime.UtcNow.AddSeconds(4));
        return Success().One(new SubsonicNode(element).Set("biography", artist.Bio)
            .AddRange(similar.Take(count).Select(a => Artist("similarArtist", a, 0, stars))));
    }

    private async Task<SubsonicNode?> TopSongsAsync()
    {
        var name = One("artist") ?? throw Missing("artist");
        var count = Int("count", 50, 1, 50);
        var (_, stars) = await LibraryAsync();
        var found = (await catalog.SearchAsync(name, Ct)).Artists.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (found is null) return Success().One(new SubsonicNode("topSongs"));
        var artist = await catalog.ArtistAsync(found.Id, Ct);
        return Success().One(new SubsonicNode("topSongs").AddRange(await SongsWithAlbumsAsync((await catalog.ExplicitTracksAsync(artist.TopTracks, Ct)).Take(count).ToArray(), stars, "song")));
    }

    // Radio: the artist's top tracks mixed with similar artists'. Without similarity data, the artist's own, shuffled.
    private async Task<SubsonicNode?> SimilarAsync(string element)
    {
        var id = One("id") ?? throw Missing("id");
        var count = Int("count", 50, 1, 50);
        string? artistId = ParseId(id, "ar");
        if (artistId is null && ParseId(id, "al") is { } releaseId) artistId = (await catalog.ReleaseAsync(releaseId, Ct)).Artists.FirstOrDefault()?.Id;
        if (artistId is null && ParseTrack(id) is not null) artistId = (await TrackAsync(id)).Track.Artists.FirstOrDefault()?.Id;
        if (artistId is null) throw NotFound("Item");
        var (_, stars) = await LibraryAsync();
        var mix = await radio.RadioAsync(artistId, count, Ct);
        if (mix.Count == 0) mix = (await catalog.ExplicitTracksAsync((await catalog.ArtistAsync(artistId, Ct)).TopTracks, Ct)).OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
        return Success().One(new SubsonicNode(element).AddRange(await SongsWithAlbumsAsync(mix, stars, "song")));
    }

    // Search hits and top tracks name their release but not its title. Titles come from the release cache, a
    // handful of lookups at most; the rest go out without an album name rather than stalling the response.
    private async Task<IEnumerable<SubsonicNode>> SongsWithAlbumsAsync(IReadOnlyList<MusicTrack> tracks, Starred stars, string element,
        IReadOnlyList<MusicRelease>? known = null, MusicLibrary? library = null)
    {
        var titles = (known ?? []).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Title);
        foreach (var play in library is null ? [] : Songs(library)) titles.TryAdd(play.Track.ReleaseId, play.Album);
        var lookups = 0;
        var deadline = DateTime.UtcNow + MusicCatalog.ExtraTime;
        foreach (var releaseId in tracks.Select(t => t.ReleaseId).Distinct().Where(r => !titles.ContainsKey(r)))
        {
            var cached = catalog.HasRelease(releaseId);
            var left = deadline - DateTime.UtcNow;
            if (lookups++ >= 8 || !cached && (!catalog.Spare || catalog.Down || left <= TimeSpan.Zero)) break;
            try { titles[releaseId] = (await catalog.ReleaseAsync(releaseId, Ct).WaitAsync(cached ? Timeout.InfiniteTimeSpan : left, Ct)).Title; }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or TimeoutException
                or OperationCanceledException && !Ct.IsCancellationRequested) { break; }
        }
        return tracks.Select(t => Song(element, t, titles.GetValueOrDefault(t.ReleaseId, ""), stars));
    }

    private async Task<SubsonicNode?> SearchAsync(string element)
    {
        var query = (One("query") ?? "").Trim().Trim('"').Trim();
        var (library, stars) = await LibraryAsync();
        int Count(string name) => Int(name + "Count", 20, 0, 500);
        int Offset(string name) => Int(name + "Offset", 0, 0, 100_000);
        var root = new SubsonicNode(element);
        // An empty query is how Symfonium and others sync "the whole library": hand them this user's own.
        if (query.Length == 0 || query == "*")
        {
            var albums = await AlbumsAsync(library);
            var counts = AlbumCounts(albums);
            root.AddRange(Artists(library).Skip(Offset("artist")).Take(Count("artist")).Select(a => Artist("artist", a, counts.GetValueOrDefault(a.Id), stars)));
            root.AddRange(albums.OrderByDescending(a => a.Added).Skip(Offset("album")).Take(Count("album")).Select(a => Album("album", a.Release, stars)));
            root.AddRange(Songs(library).Skip(Offset("song")).Take(Count("song")).Select(p => Song("song", p.Track, p.Album, stars)));
            return Success().One(root);
        }
        if (query.Length < 2) return Success().One(root);
        var results = await catalog.SearchAsync(query, Ct);
        var tracks = Count("song") > 0 ? await catalog.ExplicitTracksAsync(results.Tracks, Ct) : results.Tracks;
        var releases = MusicCatalog.PreferExplicit && Count("album") > 0 ? await catalog.WithoutCleanCopiesAsync(results.Releases, Ct) : results.Releases;
        root.AddRange(results.Artists.Skip(Offset("artist")).Take(Count("artist")).Select(a => Artist("artist", new MusicArtistRef(a.Id, a.Name), a.Releases.Count, stars)));
        root.AddRange(releases.Skip(Offset("album")).Take(Count("album")).Select(r => Album("album", r, stars)));
        root.AddRange(await SongsWithAlbumsAsync(tracks.Skip(Offset("song")).Take(Count("song")).ToArray(), stars, "song", results.Releases, library));
        return Success().One(root);
    }

    private async Task<SubsonicNode?> AlbumListAsync(string element)
    {
        var type = One("type") ?? throw Missing("type");
        var size = Int("size", 10, 1, 500);
        var offset = Int("offset", 0, 0, 100_000);
        var (library, stars) = await LibraryAsync();
        var albums = await AlbumsAsync(library);
        if (type == "highest")
        {
            // An album rated from a search may be in no other list; a stored copy describes it.
            var listed = albums.Select(a => a.Release.Id).ToHashSet();
            foreach (var id in stars.Ratings.Keys.Select(k => ParseId(k, "al")).OfType<string>().Where(id => !listed.Contains(id) && catalog.HasRelease(id)).Take(200))
                try { albums.Add(new LibraryAlbum((await catalog.ReleaseAsync(id, Ct)) with { Tracks = [] }, DateTime.UtcNow, null, 0)); }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException) { }
        }
        IEnumerable<LibraryAlbum> ordered = type switch
        {
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            "newest" => albums.OrderByDescending(a => a.Added),
            "recent" => albums.Where(a => a.Played is not null).OrderByDescending(a => a.Played),
            "frequent" => albums.Where(a => a.Plays > 0).OrderByDescending(a => a.Plays).ThenByDescending(a => a.Played),
            "highest" => albums.Where(a => stars.Rating("al-" + a.Release.Id) is not null).OrderByDescending(a => stars.Rating("al-" + a.Release.Id))
                .ThenByDescending(a => a.Plays),
            "starred" => albums.Where(a => stars.Albums.ContainsKey(a.Release.Id)).OrderByDescending(a => stars.Albums[a.Release.Id]),
            "alphabeticalByName" => albums.OrderBy(a => a.Release.Title, StringComparer.OrdinalIgnoreCase),
            "alphabeticalByArtist" => albums.OrderBy(a => SortName(Names(a.Release.Artists)), StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Release.Title, StringComparer.OrdinalIgnoreCase),
            "byYear" => ByYear(albums),
            "byGenre" => [],
            _ => throw new SubsonicError(0, "Unknown album list type: " + type)
        };
        return Success().One(new SubsonicNode(element).AddRange(ordered.Skip(offset).Take(size).Select(a => Album("album", a.Release, stars, directory: element == "albumList"))));
    }

    private IEnumerable<LibraryAlbum> ByYear(List<LibraryAlbum> albums)
    {
        var from = Int("fromYear", 0, 0, 9999);
        var to = Int("toYear", 9999, 0, 9999);
        var (low, high) = from <= to ? (from, to) : (to, from);
        var inRange = albums.Where(a => a.Release.Date?.Year is int y && y >= low && y <= high);
        return from <= to ? inRange.OrderBy(a => a.Release.Date) : inRange.OrderByDescending(a => a.Release.Date);
    }

    private async Task<SubsonicNode?> RandomSongsAsync()
    {
        var (library, stars) = await LibraryAsync();
        var size = Int("size", 10, 1, 500);
        return Success().One(new SubsonicNode("randomSongs").AddRange(Songs(library).OrderBy(_ => Random.Shared.Next()).Take(size).Select(p => Song("song", p.Track, p.Album, stars))));
    }

    private async Task<SubsonicNode?> StarredAsync(string element)
    {
        var (library, stars) = await LibraryAsync();
        var counts = AlbumCounts(await AlbumsAsync(library));
        return Success().One(new SubsonicNode(element)
            .AddRange(library.StarredArtists.Select(s => Artist("artist", s.Artist, counts.GetValueOrDefault(s.Artist.Id), stars)))
            .AddRange(library.StarredAlbums.Select(s => Album("album", s.Release, stars, directory: element == "starred")))
            .AddRange(library.StarredTracks.Select(s => Song("song", s.Track, s.Album, stars))));
    }

    // ---- Changes

    private async Task<SubsonicNode?> StarAsync(bool star)
    {
        var ids = All("id").Concat(All("albumId")).Concat(All("artistId")).Distinct().Take(500).ToArray();
        if (ids.Length == 0) throw Missing("id");
        var tracks = new List<MusicPlay>();
        var albums = new List<MusicStarredAlbum>();
        var artists = new List<MusicStarredArtist>();
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            if (!star)
            {
                if (ParseTrack(id) is var (_, trackId)) tracks.Add(new MusicPlay(new MusicTrack(trackId, "", [], "", null, null, null, false, null, null), "", now));
                else if (ParseId(id, "al") is { } releaseId) albums.Add(new MusicStarredAlbum(new MusicRelease(releaseId, "", [], "ALBUM", null, null, null, null, []), now));
                else if (ParseId(id, "ar") is { } artistId) artists.Add(new MusicStarredArtist(new MusicArtistRef(artistId, ""), now));
                else throw NotFound("Item");
                continue;
            }
            // An id that names nothing is skipped. One the catalog cannot describe right now fails the request
            // instead, before anything is saved, so the app keeps it and sends it again.
            try
            {
                if (ParseTrack(id) is not null)
                {
                    var (track, release) = await TrackAsync(id);
                    tracks.Add(new MusicPlay(track, release.Title, now));
                }
                else if (ParseId(id, "al") is { } releaseId) albums.Add(new MusicStarredAlbum(await StarredReleaseAsync(releaseId) with { Tracks = [] }, now));
                else if (ParseId(id, "ar") is { } artistId) artists.Add(new MusicStarredArtist(await StarredArtistAsync(artistId), now));
                else throw NotFound("Item");
            }
            catch (SubsonicError error) when (error.Code == 70 && ids.Length > 1) { }
        }
        if (star && tracks.Count + albums.Count + artists.Count == 0) throw NotFound("Item");
        await store.UpdateAsync(user.Id, library =>
        {
            library.StarredTracks.RemoveAll(s => tracks.Any(t => t.Track.Id == s.Track.Id));
            library.StarredAlbums.RemoveAll(s => albums.Any(a => a.Release.Id == s.Release.Id));
            library.StarredArtists.RemoveAll(s => artists.Any(a => a.Artist.Id == s.Artist.Id));
            if (!star) return;
            library.StarredTracks.InsertRange(0, tracks);
            library.StarredAlbums.InsertRange(0, albums);
            library.StarredArtists.InsertRange(0, artists);
        }, Ct);
        if (!star) await saver.CancelAsync(user.Id, albums.Select(a => a.Release.Id).ToArray(), Ct);
        else foreach (var album in albums) await saver.QueueAsync(user.Id, album.Release, Ct);
        await saver.MirrorAsync(user.Id, albums.Select(a => a.Release.Id).ToArray(), star, Ct);
        return Success();
    }

    private async Task<MusicRelease> StarredReleaseAsync(string releaseId)
    {
        if (!catalog.HasRelease(releaseId) && catalog.Down && Albums(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Release.Id == releaseId) is { } mine
            && mine.Release.Title.Length > 0) return mine.Release;
        Fetch(catalog.HasRelease(releaseId));
        return await catalog.ReleaseAsync(releaseId, Ct);
    }

    private async Task<MusicArtistRef> StarredArtistAsync(string artistId)
    {
        if (!catalog.HasArtist(artistId) && catalog.Down && Artists(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Id == artistId) is { Name.Length: > 0 } mine) return mine;
        Fetch(catalog.HasArtist(artistId));
        var artist = await catalog.ArtistAsync(artistId, Ct);
        return new MusicArtistRef(artist.Id, artist.Name);
    }

    // What each user is listening to right now, from scrobble submission=false. Shown only to that user: other
    // people's listening is theirs, as with Recently played.
    private sealed record Listening(MusicPlay Play, DateTime At, string Client);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Listening> NowPlaying = new();

    private async Task<SubsonicNode?> NowPlayingAsync()
    {
        var node = new SubsonicNode("nowPlaying");
        if (NowPlaying.TryGetValue(user.Id, out var now) && DateTime.UtcNow - now.At < TimeSpan.FromMilliseconds(Math.Max(now.Play.Track.DurationMs ?? 0, 600_000)))
        {
            var (_, stars) = await LibraryAsync();
            node.Add(Song("entry", now.Play.Track, now.Play.Album, stars).Set("username", user.Name)
                .Set("minutesAgo", (int)(DateTime.UtcNow - now.At).TotalMinutes).Set("playerId", 0).Set("playerName", now.Client));
        }
        return Success().One(node);
    }

    private async Task<SubsonicNode?> ScrobbleAsync()
    {
        var ids = All("id").Take(50).ToArray();
        if (ids.Length == 0) throw Missing("id");
        if (One("submission") is "false")
        {
            // Now playing is a courtesy: only a song already known is shown, and the app never waits on the catalog for it.
            if ((await ResolveAsync([ids[0]])).FirstOrDefault(p => p.Track.Title.Length > 0) is { } playing)
                NowPlaying[user.Id] = new Listening(playing, DateTime.UtcNow, Client());
            return Success();
        }
        var times = All("time");
        var plays = new List<MusicPlay>();
        for (var i = 0; i < ids.Length; i++)
        {
            MusicTrack track;
            MusicRelease release;
            // An app sends what it played offline in one batch. A song that does not exist is dropped; one the catalog
            // cannot describe right now fails the whole batch before anything is saved, so the app sends it again.
            try { (track, release) = await TrackAsync(ids[i]); }
            catch (SubsonicError error) when (error.Code == 70 && ids.Length > 1) { continue; }
            var at = i < times.Length && long.TryParse(times[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                && DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799999)).UtcDateTime is var when && when <= DateTime.UtcNow.AddMinutes(5)
                ? when : DateTime.UtcNow;
            plays.Add(new MusicPlay(track, release.Title, at));
        }
        await store.UpdateAsync(user.Id, library => library.Plays.InsertRange(0, plays), Ct);
        return Success();
    }

    private SubsonicNode Playlist(MusicPlaylist p, Starred stars, bool entries) => new SubsonicNode("playlist").Set("id", "pl-" + p.Id.ToString("N"))
        .Set("name", p.Name).Set("comment", p.Comment).Set("owner", user.Name).Set("public", false).Set("songCount", p.Entries.Count)
        .Set("duration", (int)Math.Round(p.Entries.Sum(e => e.Track.DurationMs ?? 0) / 1000.0)).Set("created", p.Created).Set("changed", p.Changed)
        .Set("coverArt", p.Entries.Count > 0 ? "al-" + p.Entries[0].Track.ReleaseId : null)
        .AddRange(entries ? p.Entries.Select(e => Song("entry", e.Track, e.Album, stars)) : []);

    private async Task<SubsonicNode?> PlaylistsAsync()
    {
        var (library, stars) = await LibraryAsync();
        return Success().One(new SubsonicNode("playlists").AddRange(library.Playlists.OrderByDescending(p => p.Changed).Select(p => Playlist(p, stars, false))));
    }

    private async Task<SubsonicNode?> PlaylistAsync()
    {
        var id = ParsePlaylist(One("id") ?? throw Missing("id")) ?? throw NotFound("Playlist");
        var (library, stars) = await LibraryAsync();
        return Success().One(Playlist(library.Playlists.FirstOrDefault(p => p.Id == id) ?? throw NotFound("Playlist"), stars, true));
    }

    private async Task<List<MusicPlay>> EntriesAsync(IEnumerable<string> ids)
    {
        var entries = new List<MusicPlay>();
        foreach (var id in ids.Take(MusicLibraryStore.PlaylistEntryLimit))
        {
            var (track, release) = await TrackAsync(id);
            entries.Add(new MusicPlay(track, release.Title, DateTime.UtcNow));
        }
        return entries;
    }

    private async Task<SubsonicNode?> CreatePlaylistAsync()
    {
        var existing = One("playlistId") is { } given ? ParsePlaylist(given) ?? throw NotFound("Playlist") : (Guid?)null;
        var name = One("name");
        if (existing is null && string.IsNullOrWhiteSpace(name)) throw Missing("name");
        if (All("songId").Length > MusicLibraryStore.PlaylistEntryLimit) throw TooMany();
        var entries = await EntriesAsync(All("songId"));
        var now = DateTime.UtcNow;
        var id = existing ?? Guid.NewGuid();
        bool missing = false, full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            var old = library.Playlists.FirstOrDefault(p => p.Id == id);
            if (existing is not null && old is null) { missing = true; return; }
            if (Full(library, id, entries.Count)) { full = true; return; }
            library.Playlists.RemoveAll(p => p.Id == id);
            library.Playlists.Insert(0, new MusicPlaylist(id, name ?? old!.Name, old?.Comment ?? "", old?.Created ?? now, now, entries));
        }, Ct);
        if (missing) throw NotFound("Playlist");
        if (full) throw TooMany();
        var (library, stars) = await LibraryAsync();
        return Success().One(Playlist(library.Playlists.First(p => p.Id == id), stars, true));
    }

    private static bool Full(MusicLibrary library, Guid id, int count) => count > MusicLibraryStore.PlaylistEntryLimit
        || library.Playlists.All(p => p.Id != id) && library.Playlists.Count >= MusicLibraryStore.PlaylistLimit
        || library.Playlists.Where(p => p.Id != id).Sum(p => p.Entries.Count) + count > MusicLibraryStore.PlaylistTotalLimit;
    private static SubsonicError TooMany() => new(0, $"Up to {MusicLibraryStore.PlaylistLimit} playlists, {MusicLibraryStore.PlaylistEntryLimit} songs each and {MusicLibraryStore.PlaylistTotalLimit} in all.");

    private async Task<SubsonicNode?> UpdatePlaylistAsync()
    {
        var id = ParsePlaylist(One("playlistId") ?? throw Missing("playlistId")) ?? throw NotFound("Playlist");
        var added = await EntriesAsync(All("songIdToAdd"));
        var removed = All("songIndexToRemove").Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
        bool missing = false, full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            var index = library.Playlists.FindIndex(p => p.Id == id);
            if (index < 0) { missing = true; return; }
            var p = library.Playlists[index];
            var kept = p.Entries.Where((_, i) => !removed.Contains(i)).Concat(added).ToArray();
            if (Full(library, id, kept.Length)) { full = true; return; }
            library.Playlists[index] = p with
            {
                Name = One("name") is { Length: > 0 } name ? name : p.Name,
                Comment = One("comment") ?? p.Comment,
                Changed = DateTime.UtcNow,
                Entries = kept
            };
        }, Ct);
        if (full) throw TooMany();
        return missing ? throw NotFound("Playlist") : Success();
    }

    private async Task<SubsonicNode?> DeletePlaylistAsync()
    {
        var id = ParsePlaylist(One("id") ?? throw Missing("id")) ?? throw NotFound("Playlist");
        var removed = 0;
        await store.UpdateAsync(user.Id, library => removed = library.Playlists.RemoveAll(p => p.Id == id), Ct);
        return removed == 0 ? throw NotFound("Playlist") : Success();
    }

    private string Client() => One("c") is { Length: > 0 } client ? (client.Length > 60 ? client[..60] : client) : "Subsonic";

    private async Task<SubsonicNode?> RatingAsync()
    {
        var id = One("id") ?? throw Missing("id");
        if (!MusicLibraryStore.IsRatingKey(id)) throw NotFound("Item");
        if (!int.TryParse(One("rating") ?? throw Missing("rating"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rating) || rating is < 0 or > 5)
            throw new SubsonicError(0, "A rating is 0 (none) to 5.");
        var full = false;
        await store.UpdateAsync(user.Id, library =>
        {
            if (rating == 0) library.Ratings.Remove(id);
            else if (library.Ratings.ContainsKey(id) || library.Ratings.Count < MusicLibraryStore.StarLimit) library.Ratings[id] = rating;
            else full = true;
        }, Ct);
        return full ? throw new SubsonicError(0, $"Up to {MusicLibraryStore.StarLimit} ratings are kept.") : Success();
    }

    // Songs for a queue, from what is already known: a stored album, this user's library, or a song the catalog
    // described in an earlier answer. Apps save the queue on every skip, so this never waits on the catalog; a song
    // nothing describes yet is kept by its id alone and described when the queue is read.
    private async Task<List<MusicPlay>> ResolveAsync(IEnumerable<string> ids)
    {
        var found = new List<MusicPlay>();
        foreach (var id in ids)
        {
            if (ParseTrack(id) is not var (releaseId, trackId)) continue;
            try
            {
                if (catalog.HasRelease(releaseId))
                {
                    var (track, release) = await TrackAsync(id);
                    found.Add(new MusicPlay(track, release.Title, DateTime.UtcNow));
                }
                else if (await KnownAsync(releaseId, trackId) is var (mine, album)) found.Add(new MusicPlay(mine, album.Title, DateTime.UtcNow));
                else if (catalog.KnownTrack(trackId) is { } seen && seen.ReleaseId == releaseId) found.Add(new MusicPlay(seen, "", DateTime.UtcNow));
                else found.Add(new MusicPlay(new MusicTrack(trackId, "", [], releaseId, null, null, null, false, null, null), "", DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is ProviderException or SubsonicError or HttpRequestException or JsonException) { }
        }
        return found;
    }

    // Apps save the queue on every skip and some on a timer. The latest one is kept here and written to the user's
    // library when its songs change, or at most every 30 seconds when only the position moved.
    private sealed record LiveQueue(MusicQueue Queue, DateTime Written);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LiveQueue> LiveQueues = new();

    private async Task<SubsonicNode?> SavePlayQueueAsync()
    {
        var ids = All("id").Take(MusicLibraryStore.QueueLimit).ToArray();
        if (ids.Length == 0)
        {
            LiveQueues.TryRemove(user.Id, out _);
            await store.UpdateAsync(user.Id, library => library.Queue = null, Ct, content: false);
            return Success();
        }
        var entries = await ResolveAsync(ids);
        var current = One("current") is { } given && entries.Any(e => TrackId(e.Track) == given) ? given : null;
        var position = long.TryParse(One("position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? Math.Max(0, ms) : 0;
        var queue = new MusicQueue(entries, current, position, DateTime.UtcNow, Client());
        LiveQueues.TryGetValue(user.Id, out var live);
        var same = live is not null && live.Queue.Current == current && live.Queue.Entries.Select(e => TrackId(e.Track)).SequenceEqual(entries.Select(e => TrackId(e.Track)));
        if (same && DateTime.UtcNow - live!.Written < TimeSpan.FromSeconds(30))
        {
            LiveQueues[user.Id] = live with { Queue = queue };
            return Success();
        }
        LiveQueues[user.Id] = new LiveQueue(queue, DateTime.UtcNow);
        await store.UpdateAsync(user.Id, library => library.Queue = queue, Ct, content: false);
        return Success();
    }

    private async Task<SubsonicNode?> PlayQueueAsync()
    {
        var (library, stars) = await LibraryAsync();
        if ((LiveQueues.TryGetValue(user.Id, out var live) ? live.Queue : library.Queue) is not { } queue) return Success();
        var node = new SubsonicNode("playQueue").Set("current", queue.Current).Set("position", queue.Position).Set("username", user.Name)
            .Set("changed", queue.Changed).Set("changedBy", queue.ChangedBy);
        foreach (var entry in queue.Entries)
        {
            if (entry.Track.Title.Length > 0) { node.Add(Song("entry", entry.Track, entry.Album, stars)); continue; }
            // Kept by id only: described from a copy the server has by now, otherwise left out of this answer. Reading
            // the queue never waits on the catalog either.
            if (!catalog.HasRelease(entry.Track.ReleaseId) && catalog.KnownTrack(entry.Track.Id) is null) continue;
            if ((await ResolveAsync([TrackId(entry.Track)])).FirstOrDefault(p => p.Track.Title.Length > 0) is { } known)
                node.Add(Song("entry", known.Track, known.Album, stars));
        }
        return Success().One(node);
    }

    private async Task<SubsonicNode?> BookmarksAsync()
    {
        var (library, stars) = await LibraryAsync();
        return Success().One(new SubsonicNode("bookmarks").AddRange(library.Bookmarks.Select(b => new SubsonicNode("bookmark").Set("position", b.Position)
            .Set("username", user.Name).Set("comment", b.Comment).Set("created", b.Created).Set("changed", b.Changed)
            .One(Song("entry", b.Entry.Track, b.Entry.Album, stars)))));
    }

    private async Task<SubsonicNode?> CreateBookmarkAsync()
    {
        var id = One("id") ?? throw Missing("id");
        var position = long.TryParse(One("position") ?? throw Missing("position"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? Math.Max(0, ms) : 0;
        var (track, release) = await TrackAsync(id);
        var now = DateTime.UtcNow;
        await store.UpdateAsync(user.Id, library =>
        {
            var old = library.Bookmarks.FirstOrDefault(b => b.Entry.Track.Id == track.Id);
            library.Bookmarks.RemoveAll(b => b.Entry.Track.Id == track.Id);
            library.Bookmarks.Insert(0, new MusicBookmark(new MusicPlay(track, release.Title, now), position, One("comment") ?? old?.Comment ?? "", old?.Created ?? now, now));
        }, Ct);
        return Success();
    }

    private async Task<SubsonicNode?> DeleteBookmarkAsync()
    {
        if (ParseTrack(One("id") ?? throw Missing("id")) is not var (_, trackId)) throw NotFound("Bookmark");
        var removed = 0;
        await store.UpdateAsync(user.Id, library => removed = library.Bookmarks.RemoveAll(b => b.Entry.Track.Id == trackId), Ct);
        return removed == 0 ? throw NotFound("Bookmark") : Success();
    }

    // ---- Lyrics

    private async Task<SubsonicNode?> LyricsBySongAsync()
    {
        var (track, release) = await TrackAsync(One("id"));
        var list = new SubsonicNode("lyricsList");
        if (await lyrics.TrackAsync(track, release.Title, Ct) is { } found)
        {
            var synced = found.Metadata.IsSynced == true;
            list.Add(new SubsonicNode("structuredLyrics").Set("displayArtist", Names(track.Artists)).Set("displayTitle", track.Title).Set("lang", "und")
                .Set("offset", 0).Set("synced", synced)
                .AddRange(found.Lyrics.Select(l => new SubsonicNode("line") { Text = l.Text }.Set("start", synced && l.Start is long s ? s / TimeSpan.TicksPerMillisecond : null))));
        }
        return Success().One(list);
    }

    private async Task<SubsonicNode?> LyricsAsync()
    {
        var artist = One("artist") ?? "";
        var title = One("title") ?? "";
        var node = new SubsonicNode("lyrics").Set("artist", artist).Set("title", title);
        if (title.Length == 0) return Success().One(node);
        var hit = (await catalog.SearchAsync((title + " " + artist).Trim(), Ct)).Tracks
            .FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase)
                && (artist.Length == 0 || t.Artists.Any(a => string.Equals(a.Name, artist, StringComparison.OrdinalIgnoreCase))));
        if (hit is not null)
        {
            var release = await catalog.ReleaseAsync(hit.ReleaseId, Ct);
            if (await lyrics.TrackAsync(hit, release.Title, Ct) is { } found) node.Text = string.Join('\n', found.Lyrics.Select(l => l.Text));
        }
        return Success().One(node);
    }

    // ---- Media

    private async Task<SubsonicNode?> CoverAsync()
    {
        var id = One("id") ?? throw Missing("id");
        string? address = null;
        try
        {
            // Lists ask for a cover per album: what this user's library already knows costs the catalog nothing.
            if (ParseId(id, "al") is { } releaseId)
                address = !catalog.HasRelease(releaseId) && (await AlbumsAsync(await store.ReadAsync(user.Id, Ct))).FirstOrDefault(a => a.Release.Id == releaseId) is { Release.Artwork: { } mine }
                    ? mine : (await catalog.ReleaseAsync(releaseId, Ct)).Artwork;
            else if (ParseTrack(id) is not null) address = (await TrackAsync(id)).Track.Artwork;
            else if (ParseId(id, "ar") is { } artistId) address = (await catalog.ArtistAsync(artistId, Ct)).Image;
            else if (ParsePlaylist(id) is { } playlistId)
            {
                var (library, _) = await LibraryAsync();
                address = library.Playlists.FirstOrDefault(p => p.Id == playlistId)?.Entries.FirstOrDefault()?.Track.Artwork;
            }
        }
        catch (ProviderException) { throw NotFound("Cover art"); }
        // The parsers only keep artwork on the catalog host; SafeHttp checks it again.
        if (address is null || await covers.GetAsync(address, catalog.Down, Ct) is not var (path, type)) throw NotFound("Cover art");
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotFound("Cover art"); }
        await using var _ = file;
        Response.ContentType = type;
        Response.Headers.CacheControl = "private, max-age=604800";
        // Jellyfin only adds CORS headers when a request has an Origin. Web players load a cover with <img> (no
        // Origin) and then fetch() it again to pick colours; without Vary the cached copy fails that check.
        Response.Headers.Vary = "Origin";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.ContentLength = file.Length;
        if (!HttpMethods.IsHead(Request.Method)) await file.CopyToAsync(Response.Body, Ct);
        return null;
    }

    // head, when given, receives the first bytes of the stream once (for MusicGain). reopen, when given, picks
    // the copy up again after the source drops: the catalog sometimes cuts a long download partway through.
    private async Task CopyAsync(Stream input, Stream output, long limit, CancellationToken ct, Action<byte[]>? head = null,
        Func<long, Task<Stream?>>? reopen = null)
    {
        var resumes = 0;
        var buffer = new byte[65536];
        long total = 0;
        var start = head is null ? null : new MemoryStream();
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                int read;
                try { read = await input.ReadAsync(buffer, idle.Token); }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested
                    && reopen is not null && resumes < 3)
                {
                    resumes++;
                    logger.LogInformation("StreamFin Subsonic: the catalog dropped a song after {Bytes} bytes, resuming", total);
                    input = await reopen(total) ?? throw new IOException("The catalog could not resume the song.", ex);
                    continue;
                }
                start?.Write(buffer, 0, (int)Math.Min(read, MusicGain.HeadBytes - start.Length));
                if (start is { Length: >= MusicGain.HeadBytes })
                {
                    head!(start.ToArray());
                    start = null;
                }
                if (read == 0) return;
                total += read;
                // Access is checked again as the bytes flow, so turning a user off stops a stream already running.
                if (total > limit || !Plugin.Allows(user.Id) || !SubsonicAuth.Enabled) return;
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        // A player that hangs up early, or a short range, still taught us whatever tags it covered.
        finally { if (start is { Length: > 0 }) head!(start.ToArray()); }
    }

    public enum AudioFormat { Raw, Mp3, Opus }

    // FLAC goes out untouched unless the client asks for a lossy format or a bit rate a FLAC cannot meet.
    // With preferLossless, a lossy format only counts when it comes with a real bit rate limit: apps such as
    // Amperfy ask for MP3 by default, which costs quality and makes every seek a fresh conversion.
    public static (AudioFormat Format, int Kbps) Choose(string? format, string? maxBitRate, bool preferLossless = false)
    {
        var max = int.TryParse(maxBitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 0;
        if (preferLossless && max is 0 or >= 1000) return (AudioFormat.Raw, 0);
        var kind = (format ?? "").ToLowerInvariant() switch
        {
            "raw" => AudioFormat.Raw,
            "" or "flac" => max is > 0 and < 1000 ? AudioFormat.Mp3 : AudioFormat.Raw,
            "opus" or "ogg" or "oga" => AudioFormat.Opus,
            _ => AudioFormat.Mp3
        };
        return kind switch
        {
            AudioFormat.Mp3 => (kind, Math.Clamp(max > 0 ? max : 320, 64, 320)),
            AudioFormat.Opus => (kind, Math.Clamp(max > 0 ? max : 160, 32, 256)),
            _ => (kind, 0)
        };
    }

    // Headers from the catalog within this. On a bad day it takes 20-40 s and Cloudflare answers 522 at about 40 s,
    // so a shorter limit would refuse songs that would have played. A song itself may take minutes.
    private static readonly TimeSpan HeaderLimit = TimeSpan.FromSeconds(45);

    private async Task<SubsonicNode?> StreamAsync(bool download)
    {
        var id = One("id");
        MusicTrack track;
        try { track = (await TrackAsync(id)).Track; }
        // A song kept on disk plays even when nothing describes it right now; only its length and name are unknown.
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException
            && ParseTrack(id) is var (releaseId, trackId) && songs.Has(trackId))
        {
            track = new MusicTrack(trackId, "Song", [], releaseId, null, null, null, false, null, null);
        }
        // A clean edit reached from a list or a playlist plays the explicit recording when there is one. The lookup
        // gets a few seconds: a song that has to wait on a slow catalog is worse than the edit.
        var recording = MusicCatalog.PreferExplicit ? await catalog.ExplicitAsync(track, Ct, TimeSpan.FromSeconds(4)) : track;
        var upstream = MusicCatalog.StreamUri(recording.Id);
        var (format, kbps) = download ? (AudioFormat.Raw, 0) : Choose(One("format"), One("maxBitRate"), Plugin.Instance?.Configuration.SubsonicPreferLossless != false);
        if (format == AudioFormat.Raw)
        {
            var range = Request.Headers.Range.ToString();
            if (range.Length > 0 && !(System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1))
            {
                Response.StatusCode = 416;
                return null;
            }
            // Header values must be ASCII; filename* carries the real title for "Beyoncé" and friends.
            if (download) Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = "\"" + new string(MusicPaths.Part(track.Title).Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_').ToArray()) + ".flac\"",
                FileNameStar = MusicPaths.Part(track.Title) + ".flac"
            }.ToString();
            Response.Headers.Vary = "Origin";
            songs.Warm(upstream);
            if (await songs.ServeAsync(HttpContext, upstream, range, () => Plugin.Allows(user.Id) && SubsonicAuth.Enabled)) return null;
            using var response = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, range.Length == 0 ? null : range, HttpMethods.IsHead(Request.Method)),
                Ct, attempts: 2, outages: true);
            if ((int)response.StatusCode is not (200 or 206 or 416)) throw new SubsonicError(0, "The music catalog could not stream this song.");
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = "audio/flac";
            Response.Headers.AcceptRanges = "bytes";
            Response.Headers.Vary = "Origin";
            if (response.Content.Headers.ContentLength is long length) Response.ContentLength = length;
            if (response.Content.Headers.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange.ToString();
            if (HttpMethods.IsHead(Request.Method)) return null;
            await using var input = await response.Content.ReadAsStreamAsync(Ct);
            var fromStart = response.StatusCode == System.Net.HttpStatusCode.OK || response.Content.Headers.ContentRange is { From: 0 };
            var size = response.Content.Headers.ContentRange?.Length ?? (response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null);
            var firstByte = response.Content.Headers.ContentRange?.From ?? 0;
            var lastByte = response.Content.Headers.ContentRange?.To ?? (size is long whole ? whole - 1 : (long?)null);
            var resumed = new List<HttpResponseMessage>();
            async Task<Stream?> Reopen(long done)
            {
                if (lastByte is null || firstByte + done > lastByte) return null;
                var from = firstByte + done;
                var again = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, $"bytes={from}-{lastByte}"), Ct, attempts: 3, outages: true);
                resumed.Add(again);
                // Anything but the exact bytes asked for would splice the wrong audio into the song.
                if (again.StatusCode == System.Net.HttpStatusCode.PartialContent && again.Content.Headers.ContentRange?.From == from)
                    return await again.Content.ReadAsStreamAsync(Ct);
                logger.LogWarning("StreamFin Subsonic could not resume a song at byte {Byte}: the catalog answered {Status}", from, (int)again.StatusCode);
                return null;
            }
            try { await CopyAsync(input, Response.Body, long.MaxValue, Ct, fromStart ? bytes => KeepHead(upstream, bytes, size, [track.Id, recording.Id]) : null, Reopen); }
            finally { foreach (var again in resumed) again.Dispose(); }
            return null;
        }
        // A player that hangs up on one range usually opens the next at once, so wait a little for its slot.
        for (var waited = 0; UserTranscodes.AddOrUpdate(user.Id, 1, (_, n) => n + 1) > UserTranscodeLimit; waited++)
        {
            UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1);
            if (waited >= 20) throw new SubsonicError(0, "Too many songs are being converted for you at once. Try again in a moment.");
            await Task.Delay(500, Ct);
        }
        try { await TranscodeAsync(upstream, format, kbps, recording.DurationMs, [track.Id, recording.Id]); }
        finally { UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1); }
        return null;
    }

    // FFmpeg reads the FLAC from a pipe that this method fills through SafeHttp, so FFmpeg never opens a
    // network connection of its own. Its output is a pipe as well.
    // A single byte range against a file of the given size: (first, last), or null when the header is not one.
    public static (long First, long Last)? ParseRange(string header, long size)
    {
        if (!System.Net.Http.Headers.RangeHeaderValue.TryParse(header, out var range) || range.Unit != "bytes" || range.Ranges.Count != 1) return null;
        var (from, to) = (range.Ranges.First().From, range.Ranges.First().To);
        if (from is null) return to is > 0 ? (Math.Max(0, size - to.Value), size - 1) : null;
        return (from.Value, Math.Min(to ?? size - 1, size - 1));
    }

    // MP3 at a constant bit rate is exactly kbps * 125 bytes a second, so its size is known before it exists and
    // a byte offset maps to a time. That lets it answer ranges, which Apple's player insists on: without them
    // iOS apps such as Amperfy silently refuse to play a converted song.
    private async Task TranscodeAsync(Uri upstream, AudioFormat format, int kbps, int? durationMs, string[] ids)
    {
        var offset = Int("timeOffset", 0, 0, 24 * 3600);
        songs.Warm(upstream);
        long? size = format == AudioFormat.Mp3 && durationMs is > 0 && offset == 0 ? (long)(durationMs.Value / 1000.0 * kbps * 125) : null;
        long first = 0;
        long? promised = One("estimateContentLength") == "true" && durationMs is > 0 ? (long)Math.Max(0, (durationMs.Value / 1000.0 - offset) * kbps * 125) : null;
        Response.ContentType = format == AudioFormat.Opus ? "audio/ogg" : "audio/mpeg";
        if (size is long total)
        {
            Response.Headers.AcceptRanges = "bytes";
            promised = total;
            if (Request.Headers.Range.ToString() is { Length: > 0 } header)
            {
                if (ParseRange(header, total) is not var (from, to) || from >= total || to < from)
                {
                    Response.StatusCode = 416;
                    Response.Headers.ContentRange = $"bytes */{total}";
                    return;
                }
                (first, promised) = (from, to - from + 1);
                Response.StatusCode = 206;
                Response.Headers.ContentRange = $"bytes {from}-{to}/{total}";
            }
        }
        Response.ContentLength = promised;
        if (HttpMethods.IsHead(Request.Method)) return;
        // Apple's player first asks for bytes 0-1. Every MP3 this makes starts with the same frame sync
        // (MPEG-1 layer III, no CRC, no ID3 or Xing header), so the probe needs neither the catalog nor FFmpeg.
        if (size is not null && first == 0 && promised is <= 2)
        {
            await Response.Body.WriteAsync(new byte[] { 0xFF, 0xFB }.AsMemory(0, (int)promised.Value), Ct);
            return;
        }
        var seek = offset + first / (kbps * 125.0);
        if (!await Transcodes.WaitAsync(TimeSpan.FromSeconds(10), Ct)) throw new SubsonicError(0, "The server is busy converting other songs. Try again shortly.");
        try
        {
            // A seek reads the FLAC header, then only the audio from a little before the wanted moment: FLAC frames
            // stand alone and carry their own position, so FFmpeg picks up at the next frame. Reading from the
            // start instead would fetch half of a 100 MB hi-res file to seek to its middle.
            byte[]? header = null;
            string? from = null;
            // A song already on disk feeds FFmpeg from the file: no catalog request at all.
            await using var local = songs.OpenComplete(upstream);
            if (local is not null)
            {
                if (seek > 0 && durationMs is > 0)
                {
                    var bytes = new byte[MusicGain.HeadBytes];
                    var have = await local.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, Ct);
                    if (MusicGain.AudioStart(bytes.AsSpan(0, have)) is int audio && audio < have)
                    {
                        header = bytes[..audio];
                        local.Position = audio + (long)((local.Length - audio) * Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9);
                    }
                    else local.Position = 0;
                }
            }
            else if (seek > 0 && durationMs is > 0 && FlacHeads.TryGetValue<FlacHead>(upstream.AbsoluteUri, out var known) && known is not null)
            {
                header = known.Header;
                from = $"bytes={header.Length + (long)((known.Size - header.Length) * Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9)}-";
            }
            else if (seek > 0 && durationMs is > 0)
            {
                using var head = await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, "bytes=0-65535"), Ct, attempts: 2, outages: true);
                if (head.StatusCode == System.Net.HttpStatusCode.PartialContent && head.Content.Headers.ContentRange?.Length is long flacSize)
                {
                    await using var headStream = await head.Content.ReadAsStreamAsync(Ct);
                    var bytes = new byte[MusicGain.HeadBytes];
                    var have = 0;
                    for (int got; have < bytes.Length && (got = await headStream.ReadAsync(bytes.AsMemory(have), Ct)) > 0;) have += got;
                    if (MusicGain.AudioStart(bytes.AsSpan(0, have)) is int audio && audio < have)
                    {
                        KeepHead(upstream, bytes[..have], flacSize, ids);
                        header = bytes[..audio];
                        // Bit rates vary along a song, so start early; the trim below finds the exact moment.
                        var fraction = Math.Max(0, seek - 10) / (durationMs.Value / 1000.0) * 0.9;
                        from = $"bytes={audio + (long)((flacSize - audio) * fraction)}-";
                    }
                }
            }
            using var response = local is not null ? null : await SafeHttp.PatientAsync(() => http.MediaAsync(upstream, HeaderLimit, Ct, from), Ct, attempts: 2, outages: true);
            if (response is { IsSuccessStatusCode: false }) throw new SubsonicError(0, "The music catalog could not stream this song.");
            var start = new ProcessStartInfo(encoder.EncoderPath)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            // -copyts keeps the song's own timestamps, so the trim cuts at the real moment wherever the bytes began.
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-copyts", "-f", "flac", "-i", "pipe:0", "-map", "0:a:0", "-map_metadata", "-1", "-vn" })
                start.ArgumentList.Add(arg);
            if (seek > 0) { start.ArgumentList.Add("-af"); start.ArgumentList.Add($"atrim=start={seek.ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS"); }
            foreach (var arg in format == AudioFormat.Opus
                         ? new[] { "-c:a", "libopus", "-b:a", kbps + "k", "-f", "ogg", "pipe:1" }
                         : new[] { "-c:a", "libmp3lame", "-b:a", kbps + "k", "-ar", "44100", "-write_xing", "0", "-id3v2_version", "0", "-f", "mp3", "pipe:1" })
                start.ArgumentList.Add(arg);
            Process? started;
            try { started = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { started = null; }
            using var process = started ?? throw new SubsonicError(0, "FFmpeg could not be started.");
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var errors = process.StandardError.ReadToEndAsync(abort.Token);
            var feed = Task.Run(async () =>
            {
                try
                {
                    if (header is not null) await process.StandardInput.BaseStream.WriteAsync(header, abort.Token);
                    if (response is null) await CopyAsync(local!, process.StandardInput.BaseStream, 2L * 1024 * 1024 * 1024, abort.Token);
                    else
                    {
                        await using var input = await response.Content.ReadAsStreamAsync(abort.Token);
                        var size = response.StatusCode == System.Net.HttpStatusCode.OK ? response.Content.Headers.ContentLength : null;
                        await CopyAsync(input, process.StandardInput.BaseStream, 2L * 1024 * 1024 * 1024, abort.Token, header is null ? bytes => KeepHead(upstream, bytes, size, ids) : null);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException) { }
                finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            }, abort.Token);
            try
            {
                var buffer = new byte[32768];
                int read;
                long sent = 0;
                while ((read = await process.StandardOutput.BaseStream.ReadAsync(buffer, abort.Token)) > 0)
                {
                    if (!Plugin.Allows(user.Id) || !SubsonicAuth.Enabled) break;
                    // An estimated length is a promise Kestrel enforces: the last few bytes past it are dropped.
                    if (promised is long cap) read = (int)Math.Min(read, cap - sent);
                    if (read <= 0) break;
                    await Response.Body.WriteAsync(buffer.AsMemory(0, read), abort.Token);
                    sent += read;
                }
                if (sent == 0 && !Response.HasStarted) throw new SubsonicError(0, "This song could not be converted.");
                // Short of the promise: decoders skip zero bytes while looking for the next frame.
                if (read == 0 && promised is long owed && (sent > owed / 2 || first > 0 && sent > 0))
                {
                    Array.Clear(buffer);
                    for (; sent < owed; sent += buffer.Length) await Response.Body.WriteAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, owed - sent)), abort.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            finally
            {
                abort.Cancel();
                if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
                try { await feed; } catch (OperationCanceledException) { }
                try { if (await errors is { Length: > 0 } text && process.ExitCode is not (0 or 255 or -9 or 137)) logger.LogWarning("StreamFin Subsonic transcode: {Error}", text.Length > 300 ? text[..300] : text); }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }
            }
        }
        finally { Transcodes.Release(); }
    }
}

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
