using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Api;

namespace StreamFin;

public static class MusicPaths
{
    // One path segment from catalog text: no separators or control characters, no "." or "..", and short enough
    // in bytes for any file system (a name of 80 four-byte characters would pass 255).
    public static string Part(string text)
    {
        var s = Regex.Replace(text, @"[\\/:*?""<>|\x00-\x1f\x7f]", "_", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        if (s.Length > 80) s = s[..80];
        while (Encoding.UTF8.GetByteCount(s) > 180) s = s[..^1];
        if (s.Length > 0 && char.IsHighSurrogate(s[^1])) s = s[..^1];
        // Trimmed after shortening, which can leave a space or a dot at the end.
        s = s.TrimEnd().TrimEnd('.').TrimEnd();
        return s is "" or "." or ".." ? "_" : s;
    }

    // The configured folder, or null when saving is off or the path is not absolute.
    public static string? Root(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text.Trim())) return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(text.Trim()));
    }

    public static string Album(string root, MusicRelease release)
    {
        var artist = Part(release.Artists.FirstOrDefault()?.Name ?? "Unknown artist");
        var album = Part(release.Date is { } date ? $"{release.Title} ({date.Year})" : release.Title);
        return Inside(root, Path.Combine(root, artist, album));
    }

    public static string Track(string albumFolder, MusicTrack track, bool discs)
        => Inside(albumFolder, Path.Combine(albumFolder, (discs ? $"{track.Disc ?? 1}-" : "") + $"{track.Number ?? 0:00} {Part(track.Title)}.flac"));

    private static string Inside(string root, string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new ProviderException("Unsafe music file name.");
    }
}

// Starred albums kept as FLAC files in a normal Jellyfin music library, so they play offline and in any music app.
// One album at a time, one song every 20 seconds: the catalog limits each address, and this is not urgent.
public sealed class MusicSaver(SafeHttp http, MusicCatalog catalog, MusicLibraryStore store, MusicCache songs, ILibraryManager library,
    IUserDataManager userData, ILogger<MusicSaver> logger) : BackgroundService
{
    public const string Queued = "queued", Saving = "saving", Saved = "saved", Failed = "failed";
    private static readonly TimeSpan Pace = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Busy = TimeSpan.FromMinutes(5);
    private const int BusyLimit = 6;
    // One user starring a whole discography must not hold everyone else up for weeks, or fill the disk.
    public const int QueueLimit = 200;
    private const long MinimumFree = 5L * 1024 * 1024 * 1024;
    private int turn;
    private readonly SemaphoreSlim wake = new(0, int.MaxValue);
    private DateTime lastFetch;

    public static string? Folder => MusicPaths.Root(Plugin.Instance?.Configuration.MusicLibraryFolder);
    public static bool Enabled => MusicChannel.IsAvailable && Folder is not null;

    public async Task QueueAsync(Guid user, MusicRelease release, CancellationToken ct)
    {
        if (!Enabled) return;
        await store.UpdateAsync(user, l =>
        {
            var known = l.Saves.FindIndex(s => s.ReleaseId == release.Id);
            if (known >= 0 && l.Saves[known].State is Queued or Saving or Saved) return;
            if (known >= 0) l.Saves.RemoveAt(known);
            var full = l.Saves.Count(s => s.State is Queued or Saving) >= QueueLimit;
            l.Saves.Insert(0, new MusicSave(release.Id, release.Title, string.Join(", ", release.Artists.Select(a => a.Name)), full ? Failed : Queued, 0,
                release.TrackCount ?? release.Tracks.Count, full ? $"{QueueLimit} albums are already waiting. Star this one again once they are saved." : null, DateTime.UtcNow));
        }, ct);
        wake.Release();
    }

    // Unstarring stops a save that has not finished. Files already written stay; they are the user's now, and
    // starring the album again only fetches what is missing.
    public Task CancelAsync(Guid user, IReadOnlyCollection<string> releases, CancellationToken ct) => releases.Count == 0 ? Task.CompletedTask
        : store.UpdateAsync(user, l => l.Saves.RemoveAll(s => releases.Contains(s.ReleaseId)), ct);

    public async Task<int> QueueStarredAsync(CancellationToken ct)
    {
        var count = 0;
        foreach (var user in Users())
            foreach (var star in (await store.ReadAsync(user, ct)).StarredAlbums)
            {
                await QueueAsync(user, star.Release, ct);
                count++;
            }
        return count;
    }

    private static IEnumerable<Guid> Users() => (Plugin.Instance?.Configuration.AllowedUserIds ?? []).Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
        .Where(Plugin.Allows).Distinct();

    public override Task StartAsync(CancellationToken ct)
    {
        userData.UserDataSaved += OnUserDataSaved;
        return base.StartAsync(ct);
    }

    public override Task StopAsync(CancellationToken ct)
    {
        userData.UserDataSaved -= OnUserDataSaved;
        return base.StopAsync(ct);
    }

    // A heart on a channel album in Jellyfin is the same as a star in a Subsonic app.
    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason is not (UserDataSaveReason.UpdateUserRating or UserDataSaveReason.UpdateUserData) || e.Item is not { } item || e.UserData is not { } data
            || item.ChannelId != library.GetNewItemId("Channel " + MusicChannel.ChannelName, typeof(Channel))
            || item.ExternalId?.Split('/') is not ["album", _, var releaseId] || !MusicCatalog.IsId(releaseId) || !Plugin.Allows(e.UserId)) return;
        var (user, favourite) = (e.UserId, data.IsFavorite);
        _ = Task.Run(async () =>
        {
            try
            {
                var starred = (await store.ReadAsync(user, default)).StarredAlbums.Any(s => s.Release.Id == releaseId);
                if (favourite == starred) return;
                if (!favourite)
                {
                    await store.UpdateAsync(user, l => l.StarredAlbums.RemoveAll(s => s.Release.Id == releaseId), default);
                    await CancelAsync(user, [releaseId], default);
                    return;
                }
                var release = await catalog.ReleaseAsync(releaseId, default);
                await store.UpdateAsync(user, l => l.StarredAlbums.Insert(0, new MusicStarredAlbum(release with { Tracks = [] }, DateTime.UtcNow)), default);
                await QueueAsync(user, release, default);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or TaskCanceledException)
            {
                logger.LogWarning("StreamFin could not star an album from a favourite ({ErrorType})", ex.GetType().Name);
            }
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Saves interrupted by a restart pick up where they were; finished files are skipped.
        var saved = false;
        while (!stop.IsCancellationRequested)
        {
            (Guid User, MusicSave Save)? next = null;
            // An exception leaving a background service stops Jellyfin itself, so none may.
            try
            {
                if ((next = await NextAsync(stop)) is not var (user, save))
                {
                    if (saved) Scan();
                    saved = false;
                    await wake.WaitAsync(TimeSpan.FromMinutes(30), stop);
                    continue;
                }
                saved |= await SaveAsync(user, save, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("StreamFin could not save an album to the music library ({ErrorType})", ex.GetType().Name);
                if (next is not var (user, save)) { await Task.Delay(TimeSpan.FromMinutes(1), stop).ContinueWith(_ => { }, TaskScheduler.Default); continue; }
                try { await SetAsync(user, save.ReleaseId, s => s with { State = Failed, Note = ex is ProviderException ? ex.Message : "Saving failed. Star the album again to retry." }); }
                catch (Exception) { await Task.Delay(TimeSpan.FromMinutes(1), stop).ContinueWith(_ => { }, TaskScheduler.Default); }
            }
        }
    }

    private async Task<(Guid, MusicSave)?> NextAsync(CancellationToken ct)
    {
        if (!Enabled) return null;
        // Users take turns, one album each.
        var users = Users().ToArray();
        for (var i = 0; i < users.Length; i++)
        {
            var user = users[(turn + i) % users.Length];
            if ((await store.ReadAsync(user, ct)).Saves.Where(s => s.State is Queued or Saving).MinBy(s => s.Queued) is not { } save) continue;
            turn = (turn + i + 1) % users.Length;
            return (user, save);
        }
        return null;
    }

    private Task SetAsync(Guid user, string releaseId, Func<MusicSave, MusicSave> change) => store.UpdateAsync(user, l =>
    {
        var index = l.Saves.FindIndex(s => s.ReleaseId == releaseId);
        if (index >= 0) l.Saves[index] = change(l.Saves[index]);
    }, default);

    private async Task<bool> Wanted(Guid user, string releaseId, CancellationToken ct) => Enabled && Plugin.Allows(user)
        && (await store.ReadAsync(user, ct)).Saves.Any(s => s.ReleaseId == releaseId && s.State is Queued or Saving);

    private async Task<bool> SaveAsync(Guid user, MusicSave save, CancellationToken ct)
    {
        var root = Folder ?? throw new ProviderException("No music library folder is set.");
        var release = await catalog.ReleaseAsync(save.ReleaseId, ct);
        if (release.Tracks.Count == 0) throw new ProviderException("The catalog lists no tracks for this album.");
        var folder = MusicPaths.Album(root, release);
        Directory.CreateDirectory(folder);
        await SetAsync(user, save.ReleaseId, s => s with { State = Saving, Total = release.Tracks.Count, Title = release.Title });
        await CoverAsync(release, folder, ct);
        var discs = release.Tracks.Select(t => t.Disc ?? 1).Distinct().Count() > 1;
        var done = 0;
        foreach (var track in release.Tracks)
        {
            if (!await Wanted(user, save.ReleaseId, ct)) return done > 0;
            var path = MusicPaths.Track(folder, track, discs);
            var drive = new DriveInfo(folder);
            if (drive.AvailableFreeSpace < Math.Clamp(drive.TotalSize / 20, MinimumFree, 10 * MinimumFree))
                throw new ProviderException("The music library disk is nearly full, so saving stopped. Free some space, then star the album again.");
            if (!File.Exists(path)) await SongAsync(track, path, MusicTags.For(release, track), ct);
            done++;
            await SetAsync(user, save.ReleaseId, s => s with { Saved = done });
        }
        await SetAsync(user, save.ReleaseId, s => s with { State = Saved, Note = null, Finished = DateTime.UtcNow });
        logger.LogInformation("StreamFin saved album {Release} to the music library", save.ReleaseId);
        return true;
    }

    private static readonly Dictionary<string, byte[]> Images = new()
    {
        [".jpg"] = [0xff, 0xd8], [".png"] = [0x89, 0x50, 0x4e, 0x47], [".webp"] = "RIFF"u8.ToArray()
    };

    private async Task CoverAsync(MusicRelease release, string folder, CancellationToken ct)
    {
        if (release.Artwork is not { } artwork || Path.GetExtension(new Uri(artwork).AbsolutePath) is not { } extension
            || !Images.TryGetValue(extension, out var magic)) return;
        var path = Path.Combine(folder, "cover" + extension);
        if (File.Exists(path)) return;
        try
        {
            using var response = await http.OpenAsync(new Uri(artwork), false, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 20 * 1024 * 1024) return;
            if (await WriteAsync(response, path + ".part", 20 * 1024 * 1024, magic, ct) && !File.Exists(path)) File.Move(path + ".part", path, overwrite: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ProviderException or TaskCanceledException && !ct.IsCancellationRequested) { }
        finally { Discard(path + ".part"); }
    }

    private async Task SongAsync(MusicTrack track, string path, IReadOnlyList<(string Key, string Value)> tags, CancellationToken ct)
    {
        var upstream = MusicCatalog.StreamUri(track.Id);
        var part = path + ".part";
        try
        {
            await FetchAsync(upstream, part, ct);
            await RetagAsync(part, tags, ct);
            // Never replace a file that is already there, whoever put it there.
            if (!File.Exists(path)) File.Move(part, path, overwrite: false);
        }
        finally { Discard(part); }
    }

    private async Task FetchAsync(Uri upstream, string part, CancellationToken ct)
    {
        await using (var cached = songs.OpenComplete(upstream))
            if (cached is not null)
            {
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                await cached.CopyToAsync(file, ct);
                return;
            }
        for (var busy = 0; ; busy++)
        {
            var wait = lastFetch + Pace - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            lastFetch = DateTime.UtcNow;
            using var response = await http.OpenAsync(upstream, true, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (busy + 1 >= BusyLimit) throw new ProviderException("The music catalog kept refusing downloads. Star the album again later to retry.");
                logger.LogInformation("StreamFin music library: the catalog is busy, waiting {Minutes} minutes", Busy.TotalMinutes);
                await Task.Delay(Busy, ct);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is not long size || size is <= 0 or > MusicCache.SongLimit)
                throw new ProviderException($"The catalog answered HTTP {(int)response.StatusCode} for a song of this album.");
            if (await WriteAsync(response, part, size, "fLaC"u8.ToArray(), ct, exact: true)) return;
            throw new ProviderException("A song of this album arrived incomplete. Star the album again to retry.");
        }
    }

    // The metadata is at the start and small; the new header is written, then the audio copied after it unchanged.
    private static async Task RetagAsync(string part, IReadOnlyList<(string Key, string Value)> tags, CancellationToken ct)
    {
        await using (var file = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true))
        {
            var head = new byte[(int)Math.Min(file.Length, 16 * 1024 * 1024)];
            await file.ReadExactlyAsync(head, ct);
            var audio = MusicGain.AudioStart(head) ?? throw new ProviderException("A song of this album is not a FLAC file.");
            var header = MusicTags.Retag(head, tags) ?? throw new ProviderException("A song of this album has metadata StreamFin cannot rewrite.");
            var tagged = part + ".tag";
            try
            {
                await using (var output = new FileStream(tagged, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    await output.WriteAsync(header, ct);
                    file.Position = audio;
                    await file.CopyToAsync(output, ct);
                }
                File.Move(tagged, part, overwrite: true);
            }
            finally { Discard(tagged); }
        }
    }

    // Moved into place only when whole, so the library never scans half a file.
    private static async Task<bool> WriteAsync(HttpResponseMessage response, string part, long limit, byte[] magic, CancellationToken ct, bool exact = false)
    {
        long written = 0;
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                if ((written += read) > limit) return false;
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        if (exact && written != limit) return false;
        var head = new byte[magic.Length];
        await using (var check = new FileStream(part, FileMode.Open, FileAccess.Read))
            return await check.ReadAsync(head, ct) == head.Length && head.AsSpan().SequenceEqual(magic);
    }

    private static void Discard(string part)
    {
        try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // The libraries whose folders hold the save folder; empty means Jellyfin will not see the files.
    public IReadOnlyList<string> Libraries() => Folder is not { } root ? [] : library.GetVirtualFolders()
        .Where(v => v.Locations.Any(l => MusicPaths.Root(l) is { } location && (root == location || root.StartsWith(location + Path.DirectorySeparatorChar, StringComparison.Ordinal))))
        .Select(v => v.Name).ToArray();

    private void Scan()
    {
        if (Libraries().Count == 0 || library.IsScanRunning) return;
        library.QueueLibraryScan();
    }

    public override void Dispose()
    {
        wake.Dispose();
        base.Dispose();
    }
}

public sealed record MusicSaveStatus(string User, string Album, string Artist, string State, int Saved, int Total, string? Note);
public sealed record MusicSavesResponse(string? Folder, IReadOnlyList<string> Libraries, IReadOnlyList<MusicSaveStatus> Saves);

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("StreamFin/MusicSaves")]
public sealed class MusicSavesController(MusicSaver saver, MusicLibraryStore store, MediaBrowser.Controller.Library.IUserManager users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<MusicSavesResponse>> Get(CancellationToken ct)
    {
        var saves = new List<MusicSaveStatus>();
        foreach (var id in (Plugin.Instance?.Configuration.AllowedUserIds ?? []).Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).Where(Plugin.Allows).Distinct())
        {
            var name = users.GetUserById(id)?.Username ?? id.ToString("N");
            saves.AddRange((await store.ReadAsync(id, ct)).Saves.OrderByDescending(s => s.Queued).Take(50)
                .Select(s => new MusicSaveStatus(name, s.Title, s.Artist, s.State, s.Saved, s.Total, s.Note)));
        }
        return Ok(new MusicSavesResponse(MusicSaver.Folder, saver.Libraries(), saves));
    }

    [HttpPost("QueueStarred")]
    public async Task<ActionResult<int>> QueueStarred(CancellationToken ct) => MusicSaver.Enabled ? Ok(await saver.QueueStarredAsync(ct)) : Ok(0);
}
