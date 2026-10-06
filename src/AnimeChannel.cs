using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AniFin;

public sealed class AnimeChannel(AniListCatalog catalog, IAnimeSource source, StreamRelay relay,
    IHttpContextAccessor contexts, IAuthorizationContext authorization, IMediaEncoder encoder, ILogger<AnimeChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public string Name => "Anime";
    public string Description => "AniFin · Browse anime and stream Japanese audio with English subtitles.";
    public string DataVersion => "anifin-2-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-anifin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse());

    private static ChannelItemInfo Folder(string id, string name, string? overview = null) => new()
    {
        Id = id, Name = name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container,
        Overview = overview ?? "", Tags = ["AniFin"]
    };
    private static ChannelItemResult Result(IEnumerable<ChannelItemInfo> items)
    {
        var array = items.ToArray();
        return new() { Items = array, TotalRecordCount = array.Length };
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("AniFin is not enabled for this account.");
        if (Plugin.Instance?.Configuration is not { Enabled: true, AnimeGgEnabled: true }) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("AniFin catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The catalog or source is temporarily unavailable. Please try again later.";
            // Native channels have no error banner. A non-playable status folder gives a readable explanation.
            var status = Folder(id + "/status", message, message);
            if (id.StartsWith("title/", StringComparison.Ordinal)) { status.FolderType = ChannelFolderType.Season; status.IndexNumber = 1; }
            return Result([status]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("catalog/popular/1", "Popular", "Popular anime on AniList. Source availability is checked when you open a title."),
            Folder("catalog/season/1", "This Season"), Folder("catalog/az/1", "A–Z")]);
        var parts = id.Split('/');
        if (parts is ["catalog", var category, var pageText] && int.TryParse(pageText, out var page))
        {
            var data = await catalog.BrowseAsync(category, page, ct);
            var items = data.Items.Select(anime =>
            {
                // Categories get distinct folder IDs: Jellyfin otherwise moves one folder between parents.
                var item = Folder($"title/{category}/{anime.Id}", anime.Title, anime.Overview);
                item.ImageUrl = anime.Image;
                item.ProductionYear = anime.Year;
                item.FolderType = ChannelFolderType.Series;
                // Jellyfin shares episode progress using the parent series' Custom provider ID.
                item.ProviderIds = new Dictionary<string, string> { ["Custom"] = $"anifin-series-{anime.Id}" };
                item.IndexNumber = Array.IndexOf(data.Items.ToArray(), anime) + 1;
                return item;
            }).ToList();
            if (data.HasNext && page < 1000) items.Add(Folder($"catalog/{category}/{page + 1}", $"Next page ({page + 1})"));
            return Result(items);
        }
        if (parts is ["title", var scope, var animeText] && int.TryParse(animeText, out var animeId))
        {
            var anime = await catalog.GetAsync(animeId, ct);
            var candidates = new List<SourceTitle>();
            foreach (var title in anime.Aliases.Prepend(anime.Title).Distinct().Take(3))
            {
                candidates.AddRange(await source.SearchAsync(title, ct));
                if (candidates.Any(c => AnimeGgSource.Normalize(c.Title) == AnimeGgSource.Normalize(title))) break;
            }
            var unique = candidates.DistinctBy(c => c.Id).ToArray();
            if (unique.Length == 0) throw new ProviderException("This title is not available on AnimeGG.");
            // A single exact title is safe to select; otherwise the user explicitly picks the source title.
            var names = anime.Aliases.Prepend(anime.Title).Select(AnimeGgSource.Normalize).ToHashSet();
            var exact = unique.Where(c => names.Contains(AnimeGgSource.Normalize(c.Title)) && (!c.Year.HasValue || c.Year == anime.Year)).ToArray();
            if (exact.Length == 1)
            {
                var season = Folder($"source/{scope}/{anime.Id}/{exact[0].Id}", "Episodes · AnimeGG", "Japanese audio with English subtitles.");
                season.FolderType = ChannelFolderType.Season;
                season.IndexNumber = 1;
                season.ImageUrl = anime.Image;
                return Result([season]);
            }
            return Result(unique.Select(c =>
            {
                var item = Folder($"source/{scope}/{anime.Id}/{c.Id}", $"AnimeGG · {c.Title}", "Choose the matching title to see its episodes.");
                item.FolderType = ChannelFolderType.Season;
                item.IndexNumber = Array.IndexOf(unique, c) + 1;
                return item;
            }));
        }
        if (parts is ["source", var sourceScope, var sourceAnime, var sourceId] && int.TryParse(sourceAnime, out var sourceAnimeId))
            return await EpisodesAsync(sourceScope, await catalog.GetAsync(sourceAnimeId, ct), sourceId, ct);
        return Result([]);
    }

    private async Task<ChannelItemResult> EpisodesAsync(string scope, Anime anime, string sourceId, CancellationToken ct)
    {
        var episodes = await source.EpisodesAsync(sourceId, ct);
        return Result(episodes.Select(episode => new ChannelItemInfo
        {
            Id = $"episode/{scope}/{anime.Id}/{episode.Id}",
            Name = $"{episode.Number.ToString(CultureInfo.InvariantCulture)} · {episode.Title}",
            SeriesName = anime.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode, ImageUrl = anime.Image,
            Overview = "Japanese audio · English subtitles (embedded in the video) · AnimeGG",
            IndexNumber = episode.Number == decimal.Truncate(episode.Number) ? (int)episode.Number : null,
            ParentIndexNumber = 1, ProductionYear = anime.Year,
            RunTimeTicks = anime.Duration is > 0 ? TimeSpan.FromMinutes(anime.Duration.Value).Ticks : null,
            Tags = ["AniFin"], ProviderIds = new Dictionary<string, string> { ["AniFinEpisode"] = episode.Id }
        }));
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId)) throw new UnauthorizedAccessException("AniFin is not enabled for this account.");
        var parts = id.Split('/');
        if (parts is not ["episode", _, _, var episode]) throw new ProviderException("Invalid AniFin episode.");
        var videos = await source.ResolveAsync(episode, ct);
        var ticket = relay.Create(auth.UserId, episode, videos[0]);
        var media = new MediaSourceInfo
        {
            Path = ticket.Url, Protocol = MediaProtocol.Http, Container = "mp4", IsRemote = true,
            Name = $"AnimeGG · {videos[0].Quality} · English subtitles", SupportsDirectPlay = false,
            SupportsDirectStream = false, SupportsTranscoding = true, SupportsProbing = true,
            RequiredHttpHeaders = new Dictionary<string, string> { [StreamRelay.Header] = ticket.Secret }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var info = await encoder.GetMediaInfo(new MediaInfoRequest { MediaSource = media, MediaType = DlnaProfileType.Video, ExtractChapters = false }, timeout.Token);
        media.MediaStreams = info.MediaStreams;
        media.RunTimeTicks = info.RunTimeTicks;
        media.Bitrate = info.Bitrate;
        media.Size = info.Size;
        media.Container = info.Container;
        return [media];
    }
}
