using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Item IDs: artist/<id>, top/<artist>, radio/<artist>, album/<scope>/<release>, track/<scope>/<release>/<track>.
// Jellyfin keys channel items by external ID, so the scope (where a listing found the item) keeps
// an album shared by two artists, or a track both searched and played, from moving between folders.
public sealed class MusicChannel(MusicCatalog catalog, MusicAudio sources, MusicRadio radio, MusicLibraryStore store, StreamRelay relay, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILibraryManager library, ILogger<MusicChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Music";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, MusicEnabled: true } && MusicCatalog.Api is not null;
    public string Name => ChannelName;
    public string Description => "StreamFin · Search artists and albums and stream them in lossless FLAC.";
    public string DataVersion => "streamfin-music-1-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => IsAvailable && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    // The play count is part of the key so Recently played does not wait for the catalog cache to expire.
    public string GetCacheKey(string? userId) => $"{userId}-{(Guid.TryParse(userId, out var user) ? store.Version(user) : 0)}-"
        + DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks;
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Audio], ContentTypes = [ChannelMediaContentType.Song],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(MusicChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.music.png")
    });

    private static ChannelItemInfo Folder(string id, string name, string? overview = null) => new()
    {
        Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
        Overview = overview ?? "", Tags = ["StreamFin"]
    };
    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        var array = items.ToArray();
        return new() { Items = array, TotalRecordCount = array.Length };
    }
    private static string Kind(string type) => type switch { "SINGLE" => "Single", "EP" => "EP", "COMPILATION" => "Compilation", _ => "Album" };

    public static ChannelItemInfo Artist(MusicArtistPage artist) => new()
    {
        Id = $"artist/{artist.Id}", Name = artist.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.MusicArtist,
        ImageUrl = artist.Image, Overview = artist.Bio, Tags = ["StreamFin"],
        ProviderIds = new Dictionary<string, string> { ["Custom"] = $"streamfin-artist-{artist.Id}" }
    };

    public static ChannelItemInfo Album(MusicRelease release, string scope) => new()
    {
        Id = $"album/{scope}/{release.Id}", Name = release.Title, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.MusicAlbum,
        ImageUrl = release.Artwork, ProductionYear = release.Date?.Year, PremiereDate = release.Date,
        Overview = string.Join(" · ", new[] { Kind(release.Type), release.Label }.OfType<string>()),
        Artists = release.Artists.Select(a => a.Name).ToList(), AlbumArtists = release.Artists.Select(a => a.Name).ToList(),
        Tags = ["StreamFin"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"streamfin-album-{release.Id}" }
    };

    public static ChannelItemInfo Track(MusicTrack track, string scope, IReadOnlyList<MusicArtistRef>? albumArtists = null) => new()
    {
        Id = $"track/{scope}/{track.ReleaseId}/{track.Id}", Name = track.Title, Type = ChannelItemType.Media,
        MediaType = ChannelMediaType.Audio, ContentType = ChannelMediaContentType.Song, ImageUrl = track.Artwork,
        RunTimeTicks = track.DurationMs is > 0 ? TimeSpan.FromMilliseconds(track.DurationMs.Value).Ticks : null,
        IndexNumber = track.Number, ParentIndexNumber = track.Disc,
        Artists = track.Artists.Select(a => a.Name).ToList(), AlbumArtists = (albumArtists ?? track.Artists).Select(a => a.Name).ToList(),
        Tags = ["StreamFin"], ProviderIds = Ids(track)
    };
    // The custom ID lets favourites and play counts follow a track between listings.
    public static Dictionary<string, string> Ids(MusicTrack track)
    {
        var ids = new Dictionary<string, string> { ["Custom"] = $"streamfin-track-{track.Id}" };
        if (track.Isrc is { } isrc) ids["ISRC"] = isrc;
        return ids;
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        if (!IsAvailable) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, query.UserId, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin music catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The music catalog is temporarily unavailable. Please try again later.";
            return Result([Folder(id + "/status", message, message)]);
        }
    }

    private static readonly string[] ReleaseOrder = ["ALBUM", "EP", "SINGLE", "COMPILATION"];

    private async Task<ChannelItemResult> BrowseAsync(string id, Guid user, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find artists, albums and songs online. What you find appears here."),
            Folder("starred", "Starred", "Albums you mark as a favourite here, and albums and songs you star in a Subsonic app."),
            Folder("recent", "Recently played")]);
        if (id == "search")
        {
            var parent = library.GetNewItemId(MusicSearch.FolderKey, typeof(Folder));
            return Result(library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).Select(Restore).OfType<ChannelItemInfo>());
        }
        if (id == "starred")
        {
            if (user == Guid.Empty) return Result([]);
            var starred = await store.ReadAsync(user, ct);
            return Result(starred.StarredAlbums.OrderByDescending(a => a.At).Select(a => Album(a.Release, "starred"))
                .Concat(starred.StarredTracks.OrderByDescending(t => t.At).Select(t => Track(t.Track, "starred"))));
        }
        if (id == "recent")
        {
            if (user == Guid.Empty) return Result([]);
            return Result(MusicLibraryStore.Recent(await store.ReadAsync(user, ct), 100).Select(play => Track(play.Track, "recent")));
        }
        var parts = id.Split('/');
        if (parts is ["artist", var artistId])
        {
            var artist = await catalog.ArtistAsync(artistId, ct);
            var items = new List<ChannelItemInfo>();
            if (artist.TopTracks.Count > 0)
            {
                var top = Folder($"top/{artist.Id}", "Top tracks");
                top.ImageUrl = artist.Image;
                items.Add(top);
                var mix = Folder($"radio/{artist.Id}", "Radio", $"{artist.Name} and similar artists, mixed.");
                mix.ImageUrl = artist.Image;
                items.Add(mix);
            }
            var releases = MusicCatalog.PreferExplicit ? await catalog.WithoutCleanCopiesAsync(artist.Releases, ct) : artist.Releases;
            items.AddRange(releases.OrderBy(r => System.Array.IndexOf(ReleaseOrder, r.Type)).ThenByDescending(r => r.Date).Select(r => Album(r, artist.Id)));
            return Result(items);
        }
        if (parts is ["top", var topId])
        {
            var artist = await catalog.ArtistAsync(topId, ct);
            return Result((await catalog.ExplicitTracksAsync(artist.TopTracks, ct)).Select((track, index) =>
            {
                var item = Track(track, "top-" + artist.Id);
                item.IndexNumber = index + 1;
                item.ParentIndexNumber = null;
                return item;
            }));
        }
        if (parts is ["radio", var radioId])
        {
            var mix = await radio.RadioAsync(radioId, 50, ct);
            if (mix.Count == 0) return Result([Folder(id + "/status", "No similar artists are known for this artist yet.")]);
            return Result(mix.Select((track, index) =>
            {
                var item = Track(track, "radio-" + radioId);
                item.IndexNumber = index + 1;
                item.ParentIndexNumber = null;
                return item;
            }));
        }
        if (parts is ["album", var scope, var releaseId] && IsScope(scope))
        {
            var release = await catalog.ReleaseAsync(releaseId, ct);
            return Result(release.Tracks.Select(track => Track(track with { Artwork = track.Artwork ?? release.Artwork }, "in-" + scope, release.Artists)));
        }
        return Result([]);
    }

    private static bool IsScope(string scope) => scope == "search" || MusicCatalog.IsId(scope);

    // Search results are library items made by MusicSearch; describe them again so Jellyfin keeps them.
    private static ChannelItemInfo? Restore(BaseItem item) => item switch
    {
        MusicArtist artist => new ChannelItemInfo
        {
            Id = artist.ExternalId, Name = artist.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.MusicArtist,
            Overview = artist.Overview, Tags = artist.Tags.ToList(), ProviderIds = artist.ProviderIds
        },
        MusicAlbum album => new ChannelItemInfo
        {
            Id = album.ExternalId, Name = album.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.MusicAlbum,
            Overview = album.Overview, ProductionYear = album.ProductionYear, PremiereDate = album.PremiereDate,
            AlbumArtists = album.AlbumArtists.ToList(), Artists = album.Artists.ToList(), Tags = album.Tags.ToList(), ProviderIds = album.ProviderIds
        },
        Audio audio => new ChannelItemInfo
        {
            Id = audio.ExternalId, Name = audio.Name, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Audio,
            ContentType = ChannelMediaContentType.Song, RunTimeTicks = audio.RunTimeTicks, IndexNumber = audio.IndexNumber,
            Artists = audio.Artists.ToList(), AlbumArtists = audio.AlbumArtists.ToList(), Tags = audio.Tags.ToList(), ProviderIds = audio.ProviderIds
        },
        _ => null
    };

    private async Task<(MusicTrack, string)?> KnownAsync(Guid user, string releaseId, string trackId, CancellationToken ct)
    {
        var library = await store.ReadAsync(user, ct);
        return library.Plays.Concat(library.StarredTracks).Concat(library.Playlists.SelectMany(p => p.Entries))
            .FirstOrDefault(p => p.Track.ReleaseId == releaseId && p.Track.Id == trackId && p.Track.Title.Length > 0) is { } play ? (play.Track, play.Album) : null;
    }

    public static (string Release, string Track)? ParseTrackId(string id) => id.Split('/') is ["track", _, var release, var track]
        && MusicCatalog.IsId(release) && MusicCatalog.IsId(track) ? (release, track) : null;

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        if (ParseTrackId(id) is not var (releaseId, trackId)) throw new ProviderException("Invalid StreamFin track.");
        var user = auth.UserId;
        MusicTrack track;
        string album;
        var known = await KnownAsync(user, releaseId, trackId, ct);
        // Jellyfin's player gives up after a minute, and finding the audio may take most of it. A song this user
        // already keeps does not wait long on a slow catalog for details it has.
        using var details = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (known is not null) details.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var release = await catalog.ReleaseAsync(releaseId, details.Token);
            track = release.Tracks.FirstOrDefault(t => t.Id == trackId) ?? throw new ProviderException("This track is unavailable.");
            (track, album) = (track with { Artwork = track.Artwork ?? release.Artwork }, release.Title);
        }
        // While the catalog is out, a song this user has played, starred or listed still plays from what they keep.
        catch (Exception ex) when (ex is ProviderException or System.Text.Json.JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Without that, the song's own address may still answer: Jellyfin then plays it instead of reporting
            // a format it cannot play, which is how an empty answer shows in its player.
            (track, album) = known ?? (new MusicTrack(trackId, "", [], releaseId, null, null, null, false, null, null), "");
        }
        // A few seconds for the explicit twin: Jellyfin's player waits on this answer before it starts.
        var recording = MusicCatalog.PreferExplicit ? await catalog.ExplicitAsync(track, ct, TimeSpan.FromSeconds(4)) : track;
        var ticket = await sources.ResolveAsync(recording, ct);
        var name = ticket.Lossless ? "FLAC" : $"AAC {ticket.Kbps ?? 128} · YouTube";
        // A refresh after an expired link asks the same source again: the player is partway through its file.
        static ResolvedVideo Video(AudioTicket t) => new(t.Upstream, t.Lossless ? "FLAC" : "AAC", new Dictionary<string, string>(), false, Ticket: t);
        var source = await relay.MediaSourceAsync(user, Video(ticket), name,
            async token => [Video(await sources.ResolveAsync(recording, token, ticket.Source))], () => Plugin.Allows(user) && IsAvailable, ct);
        source.RunTimeTicks ??= recording.DurationMs is > 0 ? TimeSpan.FromMilliseconds(recording.DurationMs.Value).Ticks : null;
        // Most of the catalog is 16/44.1, but some masters are 24 bit up to 96 kHz; the probe knows which.
        if (ticket.Lossless && source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Audio) is { BitDepth: > 0, SampleRate: > 0 } stream)
            source.Name = $"FLAC · {stream.BitDepth} bit / {stream.SampleRate / 1000.0:0.#} kHz";
        if (track.Title.Length > 0)
            try { await store.RecordPlayAsync(user, track, album, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { logger.LogWarning("StreamFin could not save music history ({ErrorType})", ex.GetType().Name); }
        return [source];
    }
}
