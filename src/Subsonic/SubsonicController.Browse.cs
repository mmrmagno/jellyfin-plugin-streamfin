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

public sealed partial class SubsonicController
{
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
        prefetch.Opened(user.Id, release);
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
            if (lookups++ >= 8 || !cached && (!catalog.SpareFor(releaseId) || catalog.DownFor(releaseId) || left <= TimeSpan.Zero)) break;
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
}
