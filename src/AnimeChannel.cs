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
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Drawing;

namespace StreamFin;

public sealed class AnimeChannel(AniListCatalog catalog, SourceRegistry sources, StreamRelay relay,
    IHttpContextAccessor contexts, IAuthorizationContext authorization, ILibraryManager library, ILogger<AnimeChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public string Name => "Anime";
    public string Description => "StreamFin · Browse anime and stream Japanese audio with English subtitles.";
    public string DataVersion => "streamfin-5-" + SourceRegistry.CacheKey + "-" + AniListCatalog.PolicyKey + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(AnimeChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.anime.png")
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

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        if (Plugin.Instance?.Configuration is not { Enabled: true }) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The catalog or source is temporarily unavailable. Please try again later.";
            // Native channels have no error banner. A non-playable status folder gives a readable explanation.
            var status = Folder(id + "/status", message, message);
            if (id.StartsWith("title/", StringComparison.Ordinal)) { status.FolderType = ChannelFolderType.Season; status.IndexNumber = 1; }
            if (id.StartsWith("source/", StringComparison.Ordinal))
            {
                var parent = library.GetNewItemId(id + "Anime16", typeof(Season));
                var saved = library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).OfType<Episode>();
                return Result(saved.Select(item => new ChannelItemInfo
                {
                    Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video, ContentType = ChannelMediaContentType.Episode,
                    SeriesName = item.SeriesName, Overview = item.Overview, IndexNumber = item.IndexNumber,
                    ParentIndexNumber = item.ParentIndexNumber, ProductionYear = item.ProductionYear,
                    RunTimeTicks = item.RunTimeTicks, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
                }).Append(status));
            }
            return Result([status]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find anime online. Your discovered titles appear here."),
            Folder("catalog/popular/1", "Popular", "Popular anime on AniList. Source availability is checked when you open a title."),
            Folder("catalog/season/1", "This Season"), Folder("catalog/az/1", "A–Z")]);
        var parts = id.Split('/');
        if (id == "search")
        {
            var parentId = library.GetNewItemId("searchAnime16", typeof(Folder));
            var saved = library.GetItemList(new InternalItemsQuery { ParentId = parentId, Recursive = false });
            return Result(saved.OfType<Series>().Select(item => new ChannelItemInfo
            {
                Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                // Return cleared items too: Jellyfin reconciles this list by deleting omitted entities.
                // The result filter hides cleared series while preserving episode IDs and user data.
                Overview = item.Overview, ProductionYear = item.ProductionYear, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
            }));
        }
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
                // The anifin prefix predates the rename; changing it would orphan saved progress.
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
            if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
            var matches = await sources.SearchAsync(anime, ct);
            var names = anime.Aliases.Prepend(anime.Title).Select(AnimeGgSource.Normalize).ToHashSet();
            var items = new List<ChannelItemInfo>();
            foreach (var match in matches)
            {
                foreach (var candidate in match.Titles)
                {
                    var item = Folder($"source/{scope}/{anime.Id}/{SourceRegistry.Key(match.Id, candidate.Id)}",
                        match.Titles.Count == 1 && names.Contains(AnimeGgSource.Normalize(candidate.Title)) ? $"Episodes · {match.Name}" : $"{match.Name} · {candidate.Title}",
                        "Japanese audio with English subtitles. Choose another provider here if this source is unavailable.");
                    item.FolderType = ChannelFolderType.Season;
                    // Both sources describe the same season; their episode numbers remain comparable.
                    item.IndexNumber = 1;
                    item.ImageUrl = anime.Image;
                    items.Add(item);
                }
                if (match.Titles.Count == 0)
                {
                    var message = match.Error ?? "No matching title found.";
                    var item = Folder($"title/{scope}/{anime.Id}/{match.Id}/status", $"{match.Name} · {message}", message);
                    item.FolderType = ChannelFolderType.Season;
                    item.IndexNumber = 1;
                    items.Add(item);
                }
            }
            // Jellyfin deletes omitted channel entities. Retain previously discovered source
            // folders if a provider is down/disabled, so its saved episode progress survives.
            // ContentAccess hides disabled providers from all cached and current listings.
            var parent = library.GetNewItemId(id + "Anime16", typeof(Series));
            foreach (var saved in library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).OfType<Season>())
            {
                if (!saved.ExternalId.StartsWith($"source/{scope}/{anime.Id}/", StringComparison.Ordinal)
                    || items.Any(i => i.Id == saved.ExternalId)) continue;
                var item = Folder(saved.ExternalId, saved.Name, saved.Overview);
                item.FolderType = ChannelFolderType.Season;
                item.IndexNumber = saved.IndexNumber;
                item.ProviderIds = saved.ProviderIds;
                items.Add(item);
            }
            return Result(items);
        }
        if (parts is ["source", var sourceScope, var sourceAnime, var sourceId] && int.TryParse(sourceAnime, out var sourceAnimeId))
            return await EpisodesAsync(sourceScope, await catalog.GetAsync(sourceAnimeId, ct), sourceId, ct);
        return Result([]);
    }

    private async Task<ChannelItemResult> EpisodesAsync(string scope, Anime anime, string sourceId, CancellationToken ct)
    {
        if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
        var episodes = await sources.EpisodesAsync(sourceId, anime, ct);
        var provider = SourceRegistry.Split(sourceId).Provider;
        var name = sources.Name(sourceId);
        return Result(episodes.Select(episode => new ChannelItemInfo
        {
            Id = $"episode/{scope}/{anime.Id}/{SourceRegistry.Key(provider, episode.Id)}",
            Name = $"{episode.Number.ToString(CultureInfo.InvariantCulture)} · {episode.Title}",
            SeriesName = anime.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
            ContentType = ChannelMediaContentType.Episode, ImageUrl = anime.Image,
            Overview = provider == "zoko" ? $"Japanese audio · English subtitles (selectable) · {name}" : $"Japanese audio · English subtitles (embedded in the video) · {name}",
            IndexNumber = episode.Number == decimal.Truncate(episode.Number) ? (int)episode.Number : null,
            ParentIndexNumber = 1, ProductionYear = anime.Year,
            RunTimeTicks = anime.Duration is > 0 ? TimeSpan.FromMinutes(anime.Duration.Value).Ticks : null,
            Tags = ["StreamFin"], ProviderIds = new Dictionary<string, string> { ["AniFinEpisode"] = SourceRegistry.Key(provider, episode.Id) }
        }));
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        var parts = id.Split('/');
        if (parts is not ["episode", _, var animeText, var episode] || !int.TryParse(animeText, out var animeId)) throw new ProviderException("Invalid StreamFin episode.");
        var anime = await catalog.GetAsync(animeId, ct);
        if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
        var videos = await sources.ResolveAsync(episode, ct);
        var provider = SourceRegistry.Split(episode).Provider;
        var user = auth.UserId;
        return [await relay.MediaSourceAsync(user, videos[0], $"{sources.Name(episode)} · {videos[0].Quality} · English subtitles",
            token => sources.ResolveAsync(episode, token), () => Plugin.Allows(user) && SourceRegistry.IsEnabled(provider) && AniListCatalog.IsAllowed(anime), ct)];
    }
}
