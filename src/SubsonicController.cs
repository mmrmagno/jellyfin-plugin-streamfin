using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// OpenSubsonic at <server>/StreamFin/rest/<method>[.view], for Symfonium, Feishin, Tempo, play:Sub and the like.
// Jellyfin's own sign-in does not apply here; every request carries Subsonic credentials checked by SubsonicAuth.
// IDs: ar-<artist>, al-<release>, tr-<release>-<track>, pl-<playlist>. A track needs its release, since the
// catalog only describes tracks inside releases.
[ApiController]
[Route("StreamFin/rest")]
public sealed class SubsonicController(SubsonicAuth auth, MusicCatalog catalog, MusicLibraryStore store, MusicLyrics lyrics, SafeHttp http,
    IMediaEncoder encoder, ILogger<SubsonicController> logger) : ControllerBase
{
    private const long FormLimit = 256 * 1024;
    private static readonly SemaphoreSlim Transcodes = new(4, 4);
    // So one user (or one slow client) cannot hold every slot.
    private const int UserTranscodeLimit = 2;
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
        parameters = Request.Query.ToDictionary(q => q.Key, q => q.Value.OfType<string>().ToArray(), StringComparer.Ordinal);
        if (!SubsonicAuth.Enabled) { await SendAsync(SubsonicResponse.Error(50, "The Subsonic API is turned off in the StreamFin settings.")); return; }
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
        try
        {
            if (await DispatchAsync(name) is { } node) await SendAsync(node);
        }
        catch (SubsonicError error) { await SendAsync(SubsonicResponse.Error(error.Code, error.Message)); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or RegexMatchTimeoutException or UriFormatException
            or TaskCanceledException && !HttpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin Subsonic {Method} failed ({ErrorType})", name, ex.GetType().Name);
            if (!Response.HasStarted) await SendAsync(SubsonicResponse.Error(0, ex is ProviderException ? ex.Message : "The music catalog is temporarily unavailable."));
        }
        // The catalog dropping a song mid-transfer: the client sees a cut stream, nothing more can be said.
        catch (IOException ex) when (Response.HasStarted) { logger.LogWarning("StreamFin Subsonic {Method} stopped ({ErrorType})", name, ex.GetType().Name); }
    }

    private sealed class SubsonicError(int code, string message) : Exception(message) { public int Code { get; } = code; }
    private static SubsonicError Missing(string name) => new(10, "Required parameter is missing: " + name);
    private static SubsonicError NotFound(string what) => new(70, what + " not found.");

    private async Task SendAsync(SubsonicNode node)
    {
        if (Response.HasStarted) return;
        Response.StatusCode = 200;
        Response.ContentType = Json ? "application/json; charset=utf-8" : "text/xml; charset=utf-8";
        Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(Request.Method)) await Response.Body.WriteAsync(Json ? SubsonicResponse.Json(node) : SubsonicResponse.Xml(node), HttpContext.RequestAborted);
    }

    private static SubsonicNode Success() => SubsonicResponse.Root(true);
    private CancellationToken Ct => HttpContext.RequestAborted;

    private Task<SubsonicNode?> DispatchAsync(string method) => method switch
    {
        "ping" => Done(Success()),
        "getLicense" => Done(Success().One(new SubsonicNode("license").Set("valid", true))),
        "getOpenSubsonicExtensions" => Done(Success().AddRange(new[] { "formPost", "apiKeyAuthentication", "songLyrics", "transcodeOffset" }
            .Select(e => new SubsonicNode("openSubsonicExtensions").Set("name", e).Set("versions", new[] { 1 })))),
        "getMusicFolders" => Done(Success().One(new SubsonicNode("musicFolders").Add(new SubsonicNode("musicFolder").Set("id", 1).Set("name", "StreamFin")))),
        "getUser" => Done(Success().One(UserNode())),
        "getScanStatus" or "startScan" => Done(Success().One(new SubsonicNode("scanStatus").Set("scanning", false).Set("count", 0))),
        "getGenres" => Done(Success().One(new SubsonicNode("genres"))),
        "getSongsByGenre" => Done(Success().One(new SubsonicNode("songsByGenre"))),
        "getNowPlaying" => Done(Success().One(new SubsonicNode("nowPlaying"))),
        "getBookmarks" => Done(Success().One(new SubsonicNode("bookmarks"))),
        "getPlayQueue" or "savePlayQueue" or "createBookmark" or "deleteBookmark" => Done(Success()),
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

    private SubsonicNode UserNode() => new SubsonicNode("user").Set("username", user.Name).Set("scrobblingEnabled", true).Set("adminRole", false)
        .Set("settingsRole", false).Set("downloadRole", true).Set("uploadRole", false).Set("playlistRole", true).Set("coverArtRole", true)
        .Set("commentRole", false).Set("podcastRole", false).Set("streamRole", true).Set("jukeboxRole", false).Set("shareRole", false)
        .Set("videoConversionRole", false).Set("folder", new[] { 1 });

    // ---- Library: what this user played, starred or put in a playlist. The catalog has no browsable index.

    private sealed record Starred(Dictionary<string, DateTime> Tracks, Dictionary<string, DateTime> Albums, Dictionary<string, DateTime> Artists);
    private static Starred Stars(MusicLibrary library) => new(
        library.StarredTracks.GroupBy(s => s.Track.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredAlbums.GroupBy(s => s.Release.Id).ToDictionary(g => g.Key, g => g.First().At),
        library.StarredArtists.GroupBy(s => s.Artist.Id).ToDictionary(g => g.Key, g => g.First().At));

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

    private static List<MusicArtistRef> Artists(MusicLibrary library) => library.StarredArtists.Select(s => s.Artist)
        .Concat(Songs(library).SelectMany(p => p.Track.Artists)).Concat(library.StarredAlbums.SelectMany(s => s.Release.Artists))
        .DistinctBy(a => a.Id).ToList();

    // ---- Nodes

    private static string Names(IEnumerable<MusicArtistRef> artists) => string.Join(", ", artists.Select(a => a.Name));
    private static string Kind(string type) => type switch { "SINGLE" => "Single", "EP" => "EP", "COMPILATION" => "Compilation", _ => "Album" };
    private static string PathPart(string text) => Regex.Replace(text, @"[\\/:*?""<>|\x00-\x1f]", "_", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim().TrimEnd('.') is { Length: > 0 } s
        ? (s.Length > 80 ? s[..80] : s) : "_";

    private static SubsonicNode Song(string element, MusicTrack t, string album, Starred stars)
    {
        var node = new SubsonicNode(element).Set("id", TrackId(t)).Set("parent", "al-" + t.ReleaseId).Set("isDir", false).Set("title", t.Title)
            .Set("album", album).Set("artist", Names(t.Artists)).Set("track", t.Number).Set("discNumber", t.Disc).Set("coverArt", "al-" + t.ReleaseId)
            .Set("contentType", "audio/flac").Set("suffix", "flac").Set("duration", t.DurationMs is > 0 ? (int)Math.Round(t.DurationMs.Value / 1000.0) : null)
            .Set("path", $"{PathPart(t.Artists.FirstOrDefault()?.Name ?? "Unknown")}/{PathPart(album)}/{t.Number ?? 0:00} {PathPart(t.Title)}.flac")
            .Set("albumId", "al-" + t.ReleaseId).Set("artistId", t.Artists.Count > 0 ? "ar-" + t.Artists[0].Id : null).Set("type", "music")
            .Set("mediaType", "song").Set("isVideo", false).Set("displayArtist", Names(t.Artists))
            .Set("isrc", t.Isrc is null ? null : new[] { t.Isrc }).Set("explicitStatus", t.Explicit ? "explicit" : "clean")
            .Set("starred", stars.Tracks.TryGetValue(t.Id, out var at) ? at : null);
        if (MusicGain.For(t.Id) is { } gain)
            node.One(new SubsonicNode("replayGain").Set("trackGain", gain.TrackGain).Set("albumGain", gain.AlbumGain)
                .Set("trackPeak", gain.TrackPeak).Set("albumPeak", gain.AlbumPeak));
        return node.AddRange(t.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
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
            .Set("explicitStatus", r.Tracks.Count == 0 ? null : r.Tracks.Any(t => t.Explicit) ? "explicit" : "clean");
        return node.AddRange(r.Artists.Select(a => new SubsonicNode("artists").Set("id", "ar-" + a.Id).Set("name", a.Name)));
    }

    private static SubsonicNode Artist(string element, MusicArtistRef a, int albums, Starred stars) => new SubsonicNode(element).Set("id", "ar-" + a.Id)
        .Set("name", a.Name).Set("coverArt", "ar-" + a.Id).Set("albumCount", albums).Set("starred", stars.Artists.TryGetValue(a.Id, out var at) ? at : null);

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

    private async Task<(MusicTrack Track, MusicRelease Release)> TrackAsync(string? id)
    {
        if (ParseTrack(id ?? throw Missing("id")) is not var (releaseId, trackId)) throw NotFound("Song");
        Fetch(catalog.HasRelease(releaseId));
        var release = await catalog.ReleaseAsync(releaseId, Ct);
        var track = release.Tracks.FirstOrDefault(t => t.Id == trackId) ?? throw NotFound("Song");
        return (track with { Artwork = track.Artwork ?? release.Artwork }, release);
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
        var albums = Albums(library);
        var root = new SubsonicNode(element).Set("ignoredArticles", string.Join(' ', Articles))
            .Set("lastModified", element == "indexes" ? new DateTimeOffset(DateTime.UtcNow).ToUnixTimeMilliseconds() : null);
        foreach (var group in Artists(library).GroupBy(a => SortName(a.Name) is { Length: > 0 } s && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]).ToString() : "#")
                     .OrderBy(g => g.Key == "#" ? "~" : g.Key, StringComparer.Ordinal))
            root.Add(new SubsonicNode("index").Set("name", group.Key).AddRange(group.OrderBy(a => SortName(a.Name), StringComparer.OrdinalIgnoreCase)
                .Select(a => Artist("artist", a, albums.Count(al => al.Release.Artists.Any(x => x.Id == a.Id)), stars))));
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
        return Success().One(Album("album", release, stars).AddRange(release.Tracks.Select(t => Song("song", t with { Artwork = t.Artwork ?? release.Artwork }, release.Title, stars))));
    }

    private async Task<SubsonicNode?> SongAsync()
    {
        var (track, release) = await TrackAsync(One("id"));
        var (_, stars) = await LibraryAsync();
        return Success().One(Song("song", track, release.Title, stars));
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
                .AddRange(release.Tracks.Select(t => Song("child", t, release.Title, stars))));
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
        var artist = await catalog.ArtistAsync(id, Ct);
        return Success().One(new SubsonicNode(element).Set("biography", artist.Bio));
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

    // A first version of radio: the artist's top tracks, shuffled.
    private async Task<SubsonicNode?> SimilarAsync(string element)
    {
        var id = One("id") ?? throw Missing("id");
        var count = Int("count", 50, 1, 50);
        string? artistId = ParseId(id, "ar");
        if (artistId is null && ParseId(id, "al") is { } releaseId) artistId = (await catalog.ReleaseAsync(releaseId, Ct)).Artists.FirstOrDefault()?.Id;
        if (artistId is null && ParseTrack(id) is not null) artistId = (await TrackAsync(id)).Track.Artists.FirstOrDefault()?.Id;
        if (artistId is null) throw NotFound("Item");
        var (_, stars) = await LibraryAsync();
        var top = (await catalog.ExplicitTracksAsync((await catalog.ArtistAsync(artistId, Ct)).TopTracks, Ct)).OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
        return Success().One(new SubsonicNode(element).AddRange(await SongsWithAlbumsAsync(top, stars, "song")));
    }

    // Search hits and top tracks name their release but not its title. Titles come from the release cache, a
    // handful of lookups at most; the rest go out without an album name rather than stalling the response.
    private async Task<IEnumerable<SubsonicNode>> SongsWithAlbumsAsync(IReadOnlyList<MusicTrack> tracks, Starred stars, string element,
        IReadOnlyList<MusicRelease>? known = null, MusicLibrary? library = null)
    {
        var titles = (known ?? []).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Title);
        foreach (var play in library is null ? [] : Songs(library)) titles.TryAdd(play.Track.ReleaseId, play.Album);
        var lookups = 0;
        foreach (var releaseId in tracks.Select(t => t.ReleaseId).Distinct().Where(r => !titles.ContainsKey(r)))
        {
            if (lookups++ >= 8) break;
            try { titles[releaseId] = (await catalog.ReleaseAsync(releaseId, Ct)).Title; }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException) { break; }
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
            var albums = Albums(library);
            root.AddRange(Artists(library).Skip(Offset("artist")).Take(Count("artist"))
                .Select(a => Artist("artist", a, albums.Count(al => al.Release.Artists.Any(x => x.Id == a.Id)), stars)));
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
        var albums = Albums(library);
        IEnumerable<LibraryAlbum> ordered = type switch
        {
            "random" => albums.OrderBy(_ => Random.Shared.Next()),
            "newest" => albums.OrderByDescending(a => a.Added),
            "recent" => albums.Where(a => a.Played is not null).OrderByDescending(a => a.Played),
            "frequent" or "highest" => albums.Where(a => a.Plays > 0).OrderByDescending(a => a.Plays).ThenByDescending(a => a.Played),
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
        var albums = Albums(library);
        return Success().One(new SubsonicNode(element)
            .AddRange(library.StarredArtists.Select(s => Artist("artist", s.Artist, albums.Count(al => al.Release.Artists.Any(x => x.Id == s.Artist.Id)), stars)))
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
            if (ParseTrack(id) is not null)
            {
                var (track, release) = await TrackAsync(id);
                tracks.Add(new MusicPlay(track, release.Title, now));
            }
            else if (ParseId(id, "al") is { } releaseId && Fetch(catalog.HasRelease(releaseId))) albums.Add(new MusicStarredAlbum((await catalog.ReleaseAsync(releaseId, Ct)) with { Tracks = [] }, now));
            else if (ParseId(id, "ar") is { } artistId && Fetch(catalog.HasArtist(artistId)))
            {
                var artist = await catalog.ArtistAsync(artistId, Ct);
                artists.Add(new MusicStarredArtist(new MusicArtistRef(artist.Id, artist.Name), now));
            }
            else throw NotFound("Item");
        }
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
        return Success();
    }

    private async Task<SubsonicNode?> ScrobbleAsync()
    {
        var ids = All("id").Take(50).ToArray();
        if (ids.Length == 0) throw Missing("id");
        // submission=false is "now playing", which has nowhere to go here.
        if (One("submission") is "false") return Success();
        var times = All("time");
        var plays = new List<MusicPlay>();
        for (var i = 0; i < ids.Length; i++)
        {
            var (track, release) = await TrackAsync(ids[i]);
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
        if (ParseId(id, "al") is { } releaseId) address = (await catalog.ReleaseAsync(releaseId, Ct)).Artwork;
        else if (ParseTrack(id) is not null) address = (await TrackAsync(id)).Track.Artwork;
        else if (ParseId(id, "ar") is { } artistId) address = (await catalog.ArtistAsync(artistId, Ct)).Image;
        else if (ParsePlaylist(id) is { } playlistId)
        {
            var (library, _) = await LibraryAsync();
            address = library.Playlists.FirstOrDefault(p => p.Id == playlistId)?.Entries.FirstOrDefault()?.Track.Artwork;
        }
        if (address is null) throw NotFound("Cover art");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        // The parsers only keep artwork on the catalog host; SafeHttp checks it again.
        using var response = await http.OpenAsync(new Uri(address), false, timeout.Token);
        var type = response.Content.Headers.ContentType?.MediaType;
        if (!response.IsSuccessStatusCode || type is not ("image/jpeg" or "image/png" or "image/webp")) throw NotFound("Cover art");
        if (response.Content.Headers.ContentLength is > 10 * 1024 * 1024) throw NotFound("Cover art");
        Response.ContentType = type;
        Response.Headers.CacheControl = "private, max-age=604800";
        // Jellyfin only adds CORS headers when a request has an Origin. Web players load a cover with <img> (no
        // Origin) and then fetch() it again to pick colours; without Vary the cached copy fails that check.
        Response.Headers.Vary = "Origin";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (response.Content.Headers.ContentLength is long length) Response.ContentLength = length;
        if (HttpMethods.IsHead(Request.Method)) return null;
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await CopyAsync(input, Response.Body, 10 * 1024 * 1024, timeout.Token);
        return null;
    }

    // head, when given, receives the first bytes of the stream once (for MusicGain).
    private async Task CopyAsync(Stream input, Stream output, long limit, CancellationToken ct, Action<byte[]>? head = null)
    {
        var buffer = new byte[65536];
        long total = 0;
        var start = head is null ? null : new MemoryStream();
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromSeconds(45));
                var read = await input.ReadAsync(buffer, idle.Token);
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
    public static (AudioFormat Format, int Kbps) Choose(string? format, string? maxBitRate)
    {
        var max = int.TryParse(maxBitRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : 0;
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

    private async Task<SubsonicNode?> StreamAsync(bool download)
    {
        var (track, _) = await TrackAsync(One("id"));
        // A clean edit reached from a list or a playlist plays the explicit recording when there is one.
        var recording = MusicCatalog.PreferExplicit ? await catalog.ExplicitAsync(track, Ct) : track;
        var upstream = MusicCatalog.StreamUri(recording.Id);
        var (format, kbps) = download ? (AudioFormat.Raw, 0) : Choose(One("format"), One("maxBitRate"));
        if (format == AudioFormat.Raw)
        {
            var range = Request.Headers.Range.ToString();
            if (range.Length > 0 && !(System.Net.Http.Headers.RangeHeaderValue.TryParse(range, out var parsed) && parsed.Unit == "bytes" && parsed.Ranges.Count == 1))
            {
                Response.StatusCode = 416;
                return null;
            }
            using var response = await SafeHttp.PatientAsync(() => http.OpenAsync(upstream, true, Ct, range.Length == 0 ? null : range, head: HttpMethods.IsHead(Request.Method)), Ct);
            if ((int)response.StatusCode is not (200 or 206 or 416)) throw new SubsonicError(0, "The music catalog could not stream this song.");
            Response.StatusCode = (int)response.StatusCode;
            Response.ContentType = "audio/flac";
            Response.Headers.AcceptRanges = "bytes";
            Response.Headers.Vary = "Origin";
            if (response.Content.Headers.ContentLength is long length) Response.ContentLength = length;
            if (response.Content.Headers.ContentRange is { } contentRange) Response.Headers.ContentRange = contentRange.ToString();
            // Header values must be ASCII; filename* carries the real title for "Beyoncé" and friends.
            if (download) Response.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
            {
                FileName = "\"" + new string(PathPart(track.Title).Select(c => c is >= ' ' and <= '~' and not ('"' or '\\') ? c : '_').ToArray()) + ".flac\"",
                FileNameStar = PathPart(track.Title) + ".flac"
            }.ToString();
            if (HttpMethods.IsHead(Request.Method)) return null;
            await using var input = await response.Content.ReadAsStreamAsync(Ct);
            var fromStart = response.StatusCode == System.Net.HttpStatusCode.OK || response.Content.Headers.ContentRange is { From: 0 };
            try { await CopyAsync(input, Response.Body, long.MaxValue, Ct, fromStart ? bytes => MusicGain.Remember(bytes, track.Id, recording.Id) : null); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException && Ct.IsCancellationRequested) { }
            return null;
        }
        if (UserTranscodes.AddOrUpdate(user.Id, 1, (_, n) => n + 1) > UserTranscodeLimit)
        {
            UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1);
            throw new SubsonicError(0, "You are already converting two songs. Try again when one finishes.");
        }
        try { await TranscodeAsync(upstream, format, kbps, recording.DurationMs, [track.Id, recording.Id]); }
        finally { UserTranscodes.AddOrUpdate(user.Id, 0, (_, n) => n - 1); }
        return null;
    }

    // FFmpeg reads the FLAC from a pipe that this method fills through SafeHttp, so FFmpeg never opens a
    // network connection of its own. Its output is a pipe as well.
    private async Task TranscodeAsync(Uri upstream, AudioFormat format, int kbps, int? durationMs, string[] ids)
    {
        var offset = Int("timeOffset", 0, 0, 24 * 3600);
        if (!await Transcodes.WaitAsync(TimeSpan.FromSeconds(10), Ct)) throw new SubsonicError(0, "The server is busy converting other songs. Try again shortly.");
        try
        {
            using var response = await SafeHttp.PatientAsync(() => http.OpenAsync(upstream, true, Ct), Ct);
            if (!response.IsSuccessStatusCode) throw new SubsonicError(0, "The music catalog could not stream this song.");
            var start = new ProcessStartInfo(encoder.EncoderPath)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "flac" }) start.ArgumentList.Add(arg);
            if (offset > 0) { start.ArgumentList.Add("-ss"); start.ArgumentList.Add(offset.ToString(CultureInfo.InvariantCulture)); }
            foreach (var arg in new[] { "-i", "pipe:0", "-map", "0:a:0", "-map_metadata", "-1", "-vn" }) start.ArgumentList.Add(arg);
            foreach (var arg in format == AudioFormat.Opus
                         ? new[] { "-c:a", "libopus", "-b:a", kbps + "k", "-f", "ogg", "pipe:1" }
                         : new[] { "-c:a", "libmp3lame", "-b:a", kbps + "k", "-f", "mp3", "pipe:1" })
                start.ArgumentList.Add(arg);
            Process? started;
            try { started = Process.Start(start); }
            catch (System.ComponentModel.Win32Exception) { started = null; }
            using var process = started ?? throw new SubsonicError(0, "FFmpeg could not be started.");
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            Response.ContentType = format == AudioFormat.Opus ? "audio/ogg" : "audio/mpeg";
            long? promised = One("estimateContentLength") == "true" && durationMs is > 0 ? (long)Math.Max(0, (durationMs.Value / 1000.0 - offset) * kbps * 125) : null;
            Response.ContentLength = promised;
            var errors = process.StandardError.ReadToEndAsync(abort.Token);
            var feed = Task.Run(async () =>
            {
                try
                {
                    await using var input = await response.Content.ReadAsStreamAsync(abort.Token);
                    await CopyAsync(input, process.StandardInput.BaseStream, 2L * 1024 * 1024 * 1024, abort.Token, bytes => MusicGain.Remember(bytes, ids));
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
                // Short of the estimate: decoders skip zero bytes while looking for the next frame.
                if (read == 0 && promised is long owed && sent > owed / 2)
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
