using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Every version of a Movies & TV title (one per source that has it), per user. The page lists them, the player gets the
// same list with the best one probed, and the rest are probed in the background once the title plays. Shared by the
// channel and the access filter, which puts the list into a title's page for the version picker.
public sealed class FilmVersions(TmdbCatalog catalog, FilmSources sources, StreamRelay relay, VideoHealth health, ILogger<FilmVersions> logger)
{
    // Probed is null while the versions are only listed (a page was opened); playing probes the best one.
    public sealed record Versions(IReadOnlyList<StreamRelay.Prepared> All, MediaSourceInfo? Probed)
    {
        // Always best first, so a version picked once does not become everyone's first choice on the page.
        // A version whose stream would not open in the background check is left off.
        public IReadOnlyList<MediaSourceInfo> List => All.Where(v => !v.Failed || v.Media.Id == Probed?.Id).Select(v => v.Media).ToArray();
    }

    public const string PickedKey = "StreamFin.PickedVersion";
    private static readonly MemoryCache Found = new(new MemoryCacheOptions { SizeLimit = 400 });
    private static readonly SemaphoreSlim Probing = new(1, 1);
    private readonly InFlight<Versions> listing = new(TimeSpan.FromSeconds(60));
    private static string Key(Guid user, string id) => user.ToString("N") + id;

    public static (string Kind, int Show, int? Season, int? Episode)? Parse(string? id) => id?.Split('/') switch
    {
        ["movie", _, var movie] when int.TryParse(movie, out var movieId) && movieId > 0 => ("movie", movieId, null, null),
        ["ep", _, var show, var s, var e] when int.TryParse(show, out var showId) && showId > 0 && int.TryParse(s, out var season) && int.TryParse(e, out var episode)
            && season is > 0 and < 1000 && episode is > 0 and < 10000 => ("ep", showId, season, episode),
        _ => null
    };

    public async Task<(Film Film, int? Season, int? Episode)> TargetAsync(string id, CancellationToken ct)
    {
        var parsed = Parse(id) ?? throw new ProviderException("Invalid StreamFin title.");
        return (await catalog.GetAsync(parsed.Kind == "movie" ? FilmKind.Movie : FilmKind.Tv, parsed.Show, ct), parsed.Season, parsed.Episode);
    }

    public bool TryGet(Guid user, string id, out Versions versions)
    {
        if (Found.TryGetValue<Versions>(Key(user, id), out var known) && known!.All.Count > 0 && relay.Holds(known.All[0].Media.Path)) { versions = known; return true; }
        versions = null!;
        return false;
    }
    private static void Keep(Guid user, string id, Versions versions) =>
        Found.Set(Key(user, id), versions, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });

    // For a title's page: the versions, if they are known or can be found within the wait. The lookup carries on after
    // the wait, so pressing play right after starts from it.
    public async Task<IReadOnlyList<MediaSourceInfo>?> ForPageAsync(Guid user, string id, TimeSpan wait, CancellationToken ct)
    {
        if (TryGet(user, id, out var known)) return known.List;
        if (Parse(id) is null || !FilmChannel.IsAvailable) return null;
        var lookup = listing.GetAsync(Key(user, id), token => ListAsync(user, id, token), CancellationToken.None);
        try { return (await lookup.WaitAsync(wait, ct)).List; }
        // Whatever goes wrong, the page still opens, without the version list.
        catch (Exception) when (!ct.IsCancellationRequested) { return null; }
    }

    private async Task<Versions> ListAsync(Guid user, string id, CancellationToken ct)
    {
        if (TryGet(user, id, out var known)) return known;
        var (film, season, episode) = await TargetAsync(id, ct);
        var versions = new Versions(await PrepareAsync(user, id, film, season, episode, ct), null);
        if (versions.All.Count == 0) return versions;
        Keep(user, id, versions);
        // Jellyfin keeps this list for a few minutes and plays a version picked on the page from it, so each one is
        // probed now, in the background, best first.
        ProbeOthers(versions);
        return versions;
    }

    // For playback: the versions with the best one (or the one picked on the page) probed.
    public async Task<Versions> PlayAsync(Guid user, string id, string? asked, CancellationToken ct)
    {
        if (!TryGet(user, id, out var versions))
            versions = await listing.GetAsync(Key(user, id), token => ListAsync(user, id, token), ct);
        if (versions.All.Count == 0) throw new ProviderException("No source could play this title right now. Please try again later.");
        // The version that will play is always probed: the one picked, or else the first in the list (the best).
        if (asked is { Length: > 0 } && versions.All.FirstOrDefault(v => v.Media.Id == asked) is { } picked)
            versions = versions with { Probed = picked.Probed ? picked.Media : await ProbeAsync(picked, ct) };
        else versions = await ProbeBestAsync(user, id, versions, ct);
        Keep(user, id, versions);
        return versions;
    }

    // The best version is probed; if its stream will not open, the next one is tried, up to three. One that would not
    // open is no longer offered.
    private async Task<Versions> ProbeBestAsync(Guid user, string id, Versions versions, CancellationToken ct)
    {
        var all = versions.All.ToList();
        Exception? failure = null;
        foreach (var candidate in all.Where(v => !v.Failed).Take(3).ToList())
        {
            try
            {
                var media = candidate.Probed ? candidate.Media : await ProbeAsync(candidate, ct);
                return new Versions(all, media);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { failure = ex; all.Remove(candidate); }
        }
        Found.Remove(Key(user, id));
        throw new ProviderException("The sources answered, but none of their streams would open (" + failure?.GetType().Name + "). Please try again later.");
    }

    private async Task<List<StreamRelay.Prepared>> PrepareAsync(Guid user, string id, Film film, int? season, int? episode, CancellationToken ct)
    {
        var ranked = await sources.ResolveAllAsync(film, season, episode, ct);
        foreach (var version in ranked) health.Started(version.Source.Id, version.Took);
        return ranked.Select(v => relay.Prepare(user, v.Videos[0], v.Label, VersionId(id, v.Source.Id), v.Height, v.H264,
            token => v.Source.ResolveAsync(film, season, episode, token), () => Plugin.Allows(user) && FilmChannel.IsAvailable && FilmSources.IsEnabled(v.Source.Id))).ToList();
    }

    // Gets the next episode's versions ready (listed and the best probed) while one plays.
    public async Task PrepareAsync(Guid user, string id, CancellationToken ct)
    {
        if (TryGet(user, id, out var known) && known.Probed is not null) return;
        await PlayAsync(user, id, null, ct);
    }

    // After the probe a version is named after what it really holds.
    private async Task<MediaSourceInfo> ProbeAsync(StreamRelay.Prepared version, CancellationToken ct)
    {
        var media = await relay.ProbeAsync(version, ct);
        PreferEnglish(media);
        if (media.MediaStreams.FirstOrDefault(m => m.Type == MediaStreamType.Video) is { } video)
            media.Name = FilmVersion.Name(media.Name.Split(" · ")[0], video.Width ?? 0, video.Height ?? 0,
                video.Codec is "h264" ? true : video.Codec is "hevc" or "h265" ? false : null, media.MediaStreams.Count(m => m.Type == MediaStreamType.Subtitle));
        return media;
    }

    // Some versions carry several dubs with another language first (Punkflix often starts with Hindi). English is made
    // the default audio when there is an English track; otherwise the source's own default stays.
    public static void PreferEnglish(MediaSourceInfo media)
    {
        var audio = media.MediaStreams.Where(m => m.Type == MediaStreamType.Audio).ToList();
        if (audio.Count < 2 || audio.FirstOrDefault(a => a.Language is "eng" or "en" || a.Title?.Contains("English", StringComparison.OrdinalIgnoreCase) == true) is not { } english) return;
        foreach (var track in audio) track.IsDefault = track == english;
        media.DefaultAudioStreamIndex = english.Index;
    }

    // Once a title plays, its other versions are probed one after another in the background, so switching to one starts
    // with its real streams. One title at a time server-wide; a version that will not open keeps its guessed description.
    public void ProbeOthers(Versions versions)
    {
        var waiting = versions.All.Where(v => !v.Probed).ToList();
        if (waiting.Count == 0) return;
        _ = Task.Run(async () =>
        {
            if (!await Probing.WaitAsync(0)) return;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                foreach (var version in waiting)
                {
                    try { if (!version.Probed) await ProbeAsync(version, limit.Token); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug("StreamFin films: a version could not be probed ({ErrorType})", ex.GetType().Name); }
                }
            }
            catch (OperationCanceledException) { }
            finally { Probing.Release(); }
        }, CancellationToken.None);
    }

    // Version id to title id: the app asks for a picked version as if it were an item.
    private static readonly MemoryCache Titles = new(new MemoryCacheOptions { SizeLimit = 5000 });
    public static void Remember(IEnumerable<string> versions, Guid title)
    {
        foreach (var version in versions)
            if (Guid.TryParseExact(version, "N", out var id) && id != title)
                Titles.Set(id, title, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(6) });
    }
    public static Guid? TitleOf(Guid version) => Titles.TryGetValue<Guid>(version, out var title) ? title : null;

    // A version's id stays the same for a title and site, so a choice made on the page still matches at play time.
    public static string VersionId(string item, string site) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(item + "/" + site))).ToLowerInvariant();
}
