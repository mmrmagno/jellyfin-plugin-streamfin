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
                if (started++ < 2 && catalog.SpareFor(artistId) && !catalog.DownFor(artistId)) _ = WarmArtistAsync(artistId);
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
        // Before a song plays nobody knows its source; once it has, the details say what a player will get.
        var container = MusicAudio.AllowLossy ? MusicCache.Container(t.Id) ?? MusicAudio.Known(t.Id)?.Container ?? "flac" : "flac";
        var suffix = container == "flac" ? "flac" : "m4a";
        var node = new SubsonicNode(element).Set("id", TrackId(t)).Set("parent", "al-" + t.ReleaseId).Set("isDir", false).Set("title", t.Title)
            .Set("album", album).Set("artist", Names(t.Artists)).Set("track", t.Number).Set("discNumber", t.Disc).Set("coverArt", "al-" + t.ReleaseId)
            .Set("contentType", MusicAudio.ContentType(container)).Set("suffix", suffix).Set("duration", t.DurationMs is > 0 ? (int)Math.Round(t.DurationMs.Value / 1000.0) : null)
            .Set("path", $"{MusicPaths.Part(t.Artists.FirstOrDefault()?.Name ?? "Unknown")}/{MusicPaths.Part(album)}/{t.Number ?? 0:00} {MusicPaths.Part(t.Title)}.{suffix}")
            .Set("albumId", "al-" + t.ReleaseId).Set("artistId", t.Artists.Count > 0 ? "ar-" + t.Artists[0].Id : null).Set("type", "music")
            .Set("mediaType", "song").Set("isVideo", false).Set("displayArtist", Names(t.Artists))
            .Set("isrc", t.Isrc is null ? null : new[] { t.Isrc }).Set("explicitStatus", t.Explicit ? "explicit" : "clean")
            .Set("starred", stars.Tracks.TryGetValue(t.Id, out var at) ? at : null)
            .Set("userRating", stars.Rating(TrackId(t))).Set("playCount", stars.TrackPlays.GetValueOrDefault(t.Id)?.Count)
            .Set("played", stars.TrackPlays.GetValueOrDefault(t.Id)?.Last).Set("year", release?.Date?.Year)
            .Set("displayAlbumArtist", release is { Artists.Count: > 0 } ? Names(release.Artists) : null);
        // Known once the song has passed through here: its header says how it was mastered.
        if (container == "flac" && MusicGain.FormatFor(t.Id) is { } format)
            node.Set("samplingRate", format.SampleRate).Set("bitDepth", format.BitDepth).Set("channelCount", format.Channels);
        // Amperfy hides a song whose size is unknown, and the catalog JSON has none: the real one once the song
        // has played here, otherwise an estimate from the length (lossless averages about 70% of CD rate).
        var duration = t.DurationMs is > 0 ? t.DurationMs.Value / 1000.0 : 0;
        var known = MusicCache.Size(t.Id) ?? (container == "flac"
            ? MusicCatalog.IsMonochrome(t.Id) && FlacHeads.TryGetValue<FlacHead>(MusicCatalog.StreamUri(t.Id).AbsoluteUri, out var head) ? head?.Size : null
            : MusicAudio.Known(t.Id)?.Size);
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
        if (!catalog.HasRelease(releaseId) && catalog.DownFor(releaseId) && await KnownAsync(releaseId, trackId) is { } known) return known;
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
}
