using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// One import as the import page follows it.
public sealed class ImportJob(string id, Guid user)
{
    public string Id { get; } = id;
    public Guid User { get; } = user;
    public DateTime Started { get; } = DateTime.UtcNow;
    public string Name { get; set; } = "";
    public string State { get; set; } = "reading";
    public int Total { get; set; }
    public int Found => found;
    public int Done => done;
    internal int found, done;
    public string? Message { get; set; }
    public Guid? Playlist { get; set; }
    public ConcurrentQueue<string> NotFound { get; } = new();
}

// Imports a playlist from elsewhere into a user's StreamFin playlists (Music > Playlists, and Subsonic apps): a Spotify
// playlist or album link, songs copied from the Spotify app, a YouTube or YouTube Music playlist, a Deezer playlist, a
// pasted song list or CSV export, or Spotify's own data export. Every song is matched to the catalog by ISRC when the
// source gives one, else by title, artist and length, and plays like any other. A playlist read from a link can be
// imported again later, which updates it in place.
public sealed class PlaylistImport(SafeHttp http, MusicCatalog catalog, MusicLibraryStore store, YtDlp youtube, ILogger<PlaylistImport> logger)
{
    private static readonly ConcurrentDictionary<string, ImportJob> Jobs = new();
    private static readonly MemoryCache Exports = new(new MemoryCacheOptions { SizeLimit = 50 });
    public static bool Enabled => Plugin.Instance?.Configuration is { Enabled: true, MusicEnabled: true, PlaylistImport: true };
    public static bool SpotifyLinks => Plugin.Instance?.Configuration.ImportSpotifyLinks != false;

    public static ImportJob? Job(Guid user, string id) => Jobs.TryGetValue(id, out var job) && job.User == user ? job : null;

    // Starts an import; the playlist appears under Music > Playlists at once and fills in as songs are matched. One import
    // per user at a time.
    public ImportJob Start(Guid user, string? text, string? name = null, Guid? into = null)
    {
        var (kind, reference) = ImportReaders.Detect(text);
        if (kind == ImportKind.None) throw new ProviderException("Paste a playlist link, songs copied from Spotify, or a list of songs (\"Artist - Title\", one per line).");
        if (kind is ImportKind.SpotifyPlaylist or ImportKind.SpotifyAlbum && !SpotifyLinks)
            throw new ProviderException("Spotify links are switched off on this server. Copy the songs in the Spotify app (Ctrl+A, Ctrl+C) and paste them instead.");
        if (kind == ImportKind.YouTube && !YtDlp.Wanted) throw new ProviderException("YouTube playlists need YouTube switched on under Music in the StreamFin settings.");
        return Run(user, name, into, token => ReadAsync(kind, reference, text!, token));
    }

    // Reads the playlist an earlier import came from again, into the same playlist.
    public async Task<ImportJob> ReimportAsync(Guid user, Guid playlist, CancellationToken ct)
    {
        var found = (await store.ReadAsync(user, ct)).Playlists.FirstOrDefault(p => p.Id == playlist) ?? throw new ProviderException("This playlist no longer exists.");
        var (kind, reference) = ImportReaders.FromSource(found.Source) ?? throw new ProviderException("This playlist was not imported from a link, so it cannot be read again.");
        return Run(user, found.Name, playlist, token => ReadAsync(kind, reference, "", token));
    }

    // A Spotify data export lists several playlists; they are kept a while so the page can import the ones picked.
    // Reading one is a few MB of JSON: one per user at a time, and two at a time server-wide.
    private static readonly ConcurrentDictionary<Guid, byte> Reading = new();
    private static readonly SemaphoreSlim ExportReaders = new(2, 2);
    public IReadOnlyList<ImportList> ReadExport(Guid user, string json)
    {
        if (!Reading.TryAdd(user, 0)) throw new ProviderException("An export file is already being read. Wait a moment.");
        try
        {
            if (!ExportReaders.Wait(0)) throw new ProviderException("The server is reading other export files. Try again in a moment.");
            try
            {
                var lists = ImportReaders.ParseSpotifyExport(json);
                Exports.Set(user, lists, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });
                return lists;
            }
            finally { ExportReaders.Release(); }
        }
        finally { Reading.TryRemove(user, out _); }
    }

    public ImportJob StartExport(Guid user, int index)
    {
        if (!Exports.TryGetValue<IReadOnlyList<ImportList>>(user, out var lists) || index < 0 || index >= lists!.Count)
            throw new ProviderException("Open the export file again; it is kept for half an hour.");
        var list = lists[index];
        return Run(user, list.Name, null, _ => Task.FromResult(list));
    }

    private static readonly object Starting = new();
    // Finished jobs are kept for the page to show; only the last few per user, and a bounded number in all.
    public const int KeptPerUser = 10;
    public const int KeptInAll = 500;
    // Each import asks the catalogs for up to 2000 songs; a few run at once and the rest wait their turn.
    private static readonly SemaphoreSlim Running = new(3, 3);
    private ImportJob Run(Guid user, string? name, Guid? into, Func<CancellationToken, Task<ImportList>> read)
    {
        var job = new ImportJob(Guid.NewGuid().ToString("N"), user);
        // Checked and added together: Jellyfin's app sends several searches at once for one pasted link.
        lock (Starting)
        {
            foreach (var old in Jobs.Values.Where(j => DateTime.UtcNow - j.Started > TimeSpan.FromHours(2))) Jobs.TryRemove(old.Id, out _);
            if (Jobs.Values.Any(j => j.User == user && j.State is "reading" or "matching")) throw new ProviderException("An import is already running. Wait for it to finish.");
            var finished = Jobs.Values.Where(j => j.State is "done" or "failed").OrderByDescending(j => j.Started).ToList();
            foreach (var old in finished.Where(j => j.User == user).Skip(KeptPerUser - 1).Concat(finished.Skip(KeptInAll - 1))) Jobs.TryRemove(old.Id, out _);
            Jobs[job.Id] = job;
        }
        _ = Task.Run(async () =>
        {
            // The 45 minutes start once the import gets its turn, not while it waits for one.
            using var limit = new CancellationTokenSource();
            var turn = false;
            try
            {
                turn = await Running.WaitAsync(TimeSpan.FromHours(2));
                if (!turn) throw new ProviderException("The server is busy with other imports. Please try again later.");
                limit.CancelAfter(TimeSpan.FromMinutes(45));
                await ImportAsync(job, name, into, read, limit.Token);
            }
            catch (Exception ex)
            {
                job.Message = ex is ProviderException ? ex.Message : ex is OperationCanceledException ? "The import took too long and was stopped." : "The playlist could not be read. Please try again later.";
                if (ex is not ProviderException) logger.LogWarning("StreamFin playlist import failed ({ErrorType})", ex.GetType().Name);
                // The playlist does not stay at "Importing…": it says the import stopped, and keeps the songs it has.
                if (job.Playlist is { } playlist)
                    try { await store.UpdateAsync(user, library => { var at = library.Playlists.FindIndex(p => p.Id == playlist); if (at >= 0) library.Playlists[at] = library.Playlists[at] with { Comment = ImportReaders.Clip("Import stopped: " + job.Message) }; }, CancellationToken.None); }
                    catch (Exception) { }
                job.State = "failed";
            }
            finally { if (turn) Running.Release(); }
        }, CancellationToken.None);
        return job;
    }

    private async Task ImportAsync(ImportJob job, string? name, Guid? into, Func<CancellationToken, Task<ImportList>> read, CancellationToken ct)
    {
        var list = await read(ct);
        if (list.Tracks.Count == 0) throw new ProviderException("No songs were found in what was pasted.");
        var tracks = list.Tracks.Take(MusicLibraryStore.PlaylistEntryLimit).ToList();
        job.Name = ImportReaders.Clip(string.IsNullOrWhiteSpace(name) ? list.Name : name);
        job.Total = tracks.Count;
        job.State = "matching";
        var playlist = into ?? Guid.NewGuid();
        var created = DateTime.UtcNow;
        await store.UpdateAsync(job.User, library =>
        {
            var at = library.Playlists.FindIndex(p => p.Id == playlist);
            if (at >= 0) library.Playlists[at] = library.Playlists[at] with { Comment = "Importing…", Source = list.Source ?? library.Playlists[at].Source, Changed = created };
            else if (library.Playlists.Count >= MusicLibraryStore.PlaylistLimit) throw new ProviderException("You have the most playlists StreamFin keeps. Delete one first.");
            else library.Playlists.Insert(0, new MusicPlaylist(playlist, job.Name, "Importing…", created, created, [], list.Source));
        }, ct);
        job.Playlist = playlist;

        // Three songs at a time keeps the catalog's limits; results keep the playlist's order.
        var matched = new MusicPlay?[tracks.Count];
        using var gate = new SemaphoreSlim(3);
        var lastSave = DateTime.UtcNow;
        await Task.WhenAll(tracks.Select(async (track, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (await MatchAsync(track, ct) is { } found) { matched[index] = new MusicPlay(found, track.Album ?? "", created); Interlocked.Increment(ref job.found); }
                else if (job.NotFound.Count < 300) job.NotFound.Enqueue(track.Artist + " - " + track.Title);
            }
            finally { gate.Release(); Interlocked.Increment(ref job.done); }
            // A new playlist fills in as songs are found; one imported again keeps its songs until the end.
            if (into is null && DateTime.UtcNow - lastSave > TimeSpan.FromSeconds(5))
            {
                lastSave = DateTime.UtcNow;
                await SaveAsync(job.User, playlist, matched, $"Importing… {job.Found} of {job.Total} songs found", ct);
            }
        }));
        var missed = job.Total - job.Found;
        if (into is not null && job.Found == 0)
            throw new ProviderException("No song could be matched this time (the music catalog may be down), so the playlist was left as it was.");
        var note = (list.Missing > 0 ? $" · {list.Missing} more songs could not be read" : "") + (missed > 0 ? $" · {missed} not found" : "");
        await SaveAsync(job.User, playlist, matched, $"Imported from {Origin(list.Source)} · {job.Found} of {job.Total} songs" + note, ct);
        job.Message = list.Note;
        job.State = "done";
    }

    private static string Origin(string? source) => source?.Split(':')[0] switch
    {
        "spotify" => "Spotify", "youtube" => "YouTube", "deezer" => "Deezer", _ => "a list"
    };

    private Task SaveAsync(Guid user, Guid playlist, MusicPlay?[] matched, string comment, CancellationToken ct) => store.UpdateAsync(user, library =>
    {
        var at = library.Playlists.FindIndex(p => p.Id == playlist);
        if (at < 0) return;
        var entries = matched.OfType<MusicPlay>().ToArray();
        // The whole library has a limit too; an import that would pass it keeps what fits.
        var others = library.Playlists.Where(p => p.Id != playlist).Sum(p => p.Entries.Count);
        var room = Math.Max(0, MusicLibraryStore.PlaylistTotalLimit - others);
        library.Playlists[at] = library.Playlists[at] with { Entries = entries.Take(room).ToArray(), Comment = ImportReaders.Clip(comment), Changed = DateTime.UtcNow };
    }, ct);

    private async Task<MusicTrack?> MatchAsync(ImportTrack song, CancellationToken ct)
    {
        // A catalog that times out or refuses for a moment gets one more try, a little later, before the song is left out.
        for (var attempt = 0; ; attempt++)
        {
            try { return await MatchArtistsAsync(song, ct); }
            catch (Exception ex) when (attempt == 0 && Transient(ex, ct)) { await Task.Delay(RetryDelay, ct); }
            // A song the catalogs cannot answer for is left out; the rest of the playlist still imports.
            catch (Exception ex) when (Transient(ex, ct)) { return null; }
        }
    }

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static bool Transient(Exception ex, CancellationToken ct)
        => ex is ProviderException or HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested;

    private async Task<MusicTrack?> MatchArtistsAsync(ImportTrack song, CancellationToken ct)
    {
        // An artist line like "A, B" is tried whole first (it may be one name, "Earth, Wind & Fire"), then its first artist.
        if (await MatchOneAsync(song, ct) is { } found) return found;
        var first = song.Artist.Split(new[] { ',', ';', '&' })[0].Trim();
        if (first.Length > 0 && first != song.Artist && await MatchOneAsync(song with { Artist = first }, ct) is { } byFirst) return byFirst;
        // A video titled "Label - Song (with Singer)" names its artist somewhere other than where it was looked for.
        return song.Video is { } video ? InVideo(video, song.DurationMs, (await catalog.SearchAsync(video, ct)).Tracks) : null;
    }

    private async Task<MusicTrack?> MatchOneAsync(ImportTrack song, CancellationToken ct)
    {
        if (song.Isrc is { } isrc && await catalog.ByIsrcAsync(isrc, ct) is { } exact) return exact;
        var results = (await catalog.SearchAsync(song.Artist + " " + song.Title, ct)).Tracks;
        if (MusicCatalog.BestMatch(song.Title, song.Artist, song.DurationMs, results) is { } hit) return hit;
        // Free-text search often misses a named song; Deezer's artist and title search finds it.
        var named = await catalog.SongSearchAsync(song.Artist, Plain(song.Title), ct);
        if (MusicCatalog.BestMatch(song.Title, song.Artist, song.DurationMs, named) is { } byName) return byName;
        // Some catalogs file a song under a featured artist first; the title alone finds those.
        var more = results.Concat(named).Concat((await catalog.SearchAsync(song.Title, ct)).Tracks).ToList();
        return MusicCatalog.BestMatch(song.Title, song.Artist, song.DurationMs, more) ?? Bare(song, more, 3000) ?? Looser(song, more);
    }

    // A result whose title and one of whose artists both appear, as whole words, in the video's title and channel,
    // with a length within 15 seconds when both are known. The longest title wins, so "Kalyani" beats "Ka".
    public static MusicTrack? InVideo(string video, int? durationMs, IEnumerable<MusicTrack> candidates)
    {
        var words = " " + MusicAudio.Normalize(video) + " ";
        bool Has(string text) => MusicAudio.Normalize(Plain(text)) is { Length: >= 2 } part && words.Contains(" " + part + " ", StringComparison.Ordinal);
        return candidates.Where(c => Has(c.Title) && c.Artists.Any(a => Has(a.Name))
                && (durationMs is null || c.DurationMs is null || Math.Abs(c.DurationMs.Value - durationMs.Value) <= 15_000))
            .OrderByDescending(c => Plain(c.Title).Length).FirstOrDefault();
    }

    // The title as a search wants it: without " - Remastered 2011", "(feat. ...)" or similar extras.
    public static string Plain(string title)
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(title.Split(" - ")[0], @"\s*[(\[][^)\]]*[)\]]", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        return bare.Length > 0 ? bare : title.Trim();
    }

    // Titles compared without anything in brackets or after " - " on either side ("Death On Two Legs (Dedicated To...)"
    // is "Death on Two Legs"), the closest length first and within the window; the artist must still be the same.
    public static MusicTrack? Bare(ImportTrack song, IEnumerable<MusicTrack> candidates, int windowMs)
    {
        var ordered = candidates.Where(c => song.DurationMs is not int d || c.DurationMs is not int x || Math.Abs(x - d) <= windowMs)
            .OrderBy(c => song.DurationMs is int d && c.DurationMs is int x ? Math.Abs(x - d) : int.MaxValue).ToList();
        var found = MusicCatalog.BestMatch(Plain(song.Title), song.Artist, null, ordered.Select(c => c with { Title = Plain(c.Title) }));
        return found is null ? null : ordered.First(c => c.Id == found.Id && c.ReleaseId == found.ReleaseId);
    }

    // A playlist often has the single or radio edit where the catalog has the album version: same title and artist, a
    // different length. Then the closest length within a minute is taken.
    public static MusicTrack? Looser(ImportTrack song, IEnumerable<MusicTrack> candidates) => Bare(song, candidates, 60_000);

    private async Task<ImportList> ReadAsync(ImportKind kind, string reference, string text, CancellationToken ct) => kind switch
    {
        ImportKind.SpotifyPlaylist or ImportKind.SpotifyAlbum => await SpotifyAsync(kind, reference, ct),
        ImportKind.SpotifyTracks => await SpotifyTracksAsync(ImportReaders.SpotifyTrackIds(text), ct),
        ImportKind.YouTube => await YouTubeAsync(reference, ct),
        ImportKind.Deezer => await DeezerAsync(reference, ct),
        ImportKind.Csv => new ImportList("Imported playlist", ImportReaders.ParseCsv(text), null),
        _ => new ImportList("Imported playlist", ImportReaders.ParseText(text), null)
    };

    // Spotify's embed page: up to 100 songs, no account needed. A longer playlist says how many are left over.
    private async Task<ImportList> SpotifyAsync(ImportKind kind, string id, CancellationToken ct)
    {
        var path = kind == ImportKind.SpotifyAlbum ? "album/" : "playlist/";
        string html;
        try { html = await http.TextAsync(new Uri("https://open.spotify.com/embed/" + path + id), ct, open: true); }
        catch (SourceStatusException) { throw new ProviderException("Spotify does not share this playlist. Is it public?"); }
        var (name, tracks) = ImportReaders.ParseSpotifyEmbed(html);
        var missing = 0;
        if (kind == ImportKind.SpotifyPlaylist && tracks.Count >= 100)
        {
            try { missing = Math.Max(0, (ImportReaders.SpotifyTotal(await http.TextAsync(new Uri("https://open.spotify.com/playlist/" + id), ct, open: true)) ?? 0) - tracks.Count); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException) { }
        }
        return new ImportList(name, tracks, ImportReaders.SourceOf(kind, id), missing,
            missing > 0 ? $"Spotify only shares the first {tracks.Count} songs of a playlist this way. For all of them, open the playlist in the Spotify app, press Ctrl+A then Ctrl+C, and paste here." : null);
    }

    // Songs copied in the Spotify app arrive as one link each; each song's page names it.
    private async Task<ImportList> SpotifyTracksAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var tracks = new ImportTrack?[ids.Count];
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(ids.Select(async (id, index) =>
        {
            await gate.WaitAsync(ct);
            try { tracks[index] = ImportReaders.ParseSpotifyTrackPage(await http.TextAsync(new Uri("https://open.spotify.com/track/" + id), ct, open: true)); }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { }
            finally { gate.Release(); }
        }));
        return new ImportList("Spotify songs", tracks.OfType<ImportTrack>().ToList(), null);
    }

    private async Task<ImportList> YouTubeAsync(string list, CancellationToken ct)
    {
        await youtube.EnsureAsync(ct);
        if (YtDlp.DenoFile is not { } deno) throw new ProviderException("YouTube is not set up on this server.");
        var (name, tracks) = ImportReaders.ParseYouTube(await youtube.RunAsync(YtDlp.PlaylistArguments(deno, list), TimeSpan.FromMinutes(3), ct));
        return new ImportList(name, tracks, ImportReaders.SourceOf(ImportKind.YouTube, list));
    }

    private async Task<ImportList> DeezerAsync(string id, CancellationToken ct)
    {
        var name = ImportReaders.DeezerName(await http.TextAsync(new Uri("https://api.deezer.com/playlist/" + id), ct, open: true));
        var tracks = new List<ImportTrack>();
        for (var index = 0; tracks.Count < ImportReaders.TrackLimit; index += 100)
        {
            var (page, more) = ImportReaders.ParseDeezerTracks(await http.TextAsync(new Uri($"https://api.deezer.com/playlist/{id}/tracks?limit=100&index={index}"), ct, open: true));
            tracks.AddRange(page);
            if (!more || page.Count == 0) break;
        }
        return new ImportList(name, tracks, ImportReaders.SourceOf(ImportKind.Deezer, id));
    }
}
