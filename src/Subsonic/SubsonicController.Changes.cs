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
        if (!catalog.HasRelease(releaseId) && catalog.DownFor(releaseId) && Albums(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Release.Id == releaseId) is { } mine
            && mine.Release.Title.Length > 0) return mine.Release;
        Fetch(catalog.HasRelease(releaseId));
        return await catalog.ReleaseAsync(releaseId, Ct);
    }

    private async Task<MusicArtistRef> StarredArtistAsync(string artistId)
    {
        if (!catalog.HasArtist(artistId) && catalog.DownFor(artistId) && Artists(await store.ReadAsync(user.Id, Ct)).FirstOrDefault(a => a.Id == artistId) is { Name.Length: > 0 } mine) return mine;
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
        await store.RecordPlaysAsync(user.Id, plays, Ct);
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
        return Success().One(new SubsonicNode("playlists").AddRange(library.Playlists.OrderByDescending(p => p.Changed)
            .Concat(await RecapPlaylistsAsync()).Select(p => Playlist(p, stars, false))));
    }

    // Each year's most played songs as a playlist an app can open but not change: its id is never stored, so every
    // edit answers "not found".
    public static Guid RecapPlaylistId(int year) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes("streamfin-recap-" + year)));
    private async Task<IReadOnlyList<MusicPlaylist>> RecapPlaylistsAsync()
    {
        var playlists = new List<MusicPlaylist>();
        foreach (var year in store.History.Years(user.Id).Take(5))
        {
            var recap = MusicRecap.Build(year, await store.History.ReadAsync(user.Id, year, Ct));
            if (recap.Plays == 0) continue;
            var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            playlists.Add(new MusicPlaylist(RecapPlaylistId(year), $"Your {year}: top songs", recap.Summary, start, DateTime.UtcNow,
                recap.TopSongs.Select(song => new MusicPlay(song.Track, song.Album, start)).ToArray()));
        }
        return playlists;
    }

    private async Task<SubsonicNode?> PlaylistAsync()
    {
        var id = ParsePlaylist(One("id") ?? throw Missing("id")) ?? throw NotFound("Playlist");
        var (library, stars) = await LibraryAsync();
        var found = library.Playlists.FirstOrDefault(p => p.Id == id) ?? (await RecapPlaylistsAsync()).FirstOrDefault(p => p.Id == id);
        return Success().One(Playlist(found ?? throw NotFound("Playlist"), stars, true));
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
}
