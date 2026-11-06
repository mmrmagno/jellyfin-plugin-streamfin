using Microsoft.Extensions.Caching.Memory;
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
    public string DataVersion => "streamfin-6-" + SourceRegistry.CacheKey + "-" + AniListCatalog.PolicyKey + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    // A site going down or coming back changes what a title lists, so its cached listings are made again.
    public string GetCacheKey(string? userId) => $"{userId}-{sources.Health.Key}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
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
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
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
            var data = await Pages.LoadAsync(page, PagesPerFolder, 1000, number => catalog.BrowseAsync(category, number, ct), d => d.Items, d => d.HasNext, a => a.Id);
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
                item.IndexNumber = data.Items.IndexOf(anime) + 1;
                return item;
            }).ToList();
            if (data.Next is { } next) items.Add(Folder($"catalog/{category}/{next}", $"More ({(next - 1) / PagesPerFolder + 1})"));
            return Result(items);
        }
        if (parts is ["title", var scope, var animeText] && int.TryParse(animeText, out var animeId))
        {
            var anime = await catalog.GetAsync(animeId, ct);
            if (!AniListCatalog.IsAllowed(anime)) throw new ProviderException("This title is hidden by the content filter.");
            var matches = await sources.SearchAsync(anime, ct);
            var items = new List<ChannelItemInfo>();
            foreach (var match in matches)
            {
                foreach (var candidate in match.Titles)
                {
                    var item = Folder($"source/{scope}/{anime.Id}/{SourceRegistry.Key(match.Id, candidate.Id)}",
                        match.Titles.Count == 1 && SourceRegistry.IsExact(candidate, anime) ? $"Episodes · {match.Name}" : $"{match.Name} · {candidate.Title}",
                        "Japanese audio with English subtitles. Choose another provider here if this source is unavailable.");
                    item.FolderType = ChannelFolderType.Season;
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
            // The AniFin provider key predates the rename; watch state is matched on it.
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
        var user = auth.UserId;
        var key = user.ToString("N") + id;
        // Only a real start prepares the next episode, not opening an episode's page.
        var starting = context.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;
        if (PreparesNext && Ready.TryGetValue<Prepared>(key, out var ready) && SourceRegistry.IsEnabled(ready!.Provider) && relay.Holds(ready.Source.Path))
        {
            if (starting) PrepareNext(user, parts[1], anime, episode);
            return [ready.Source];
        }
        var source = await PlayAsync(user, parts[1], anime, episode, EpisodeNumber(id), ct);
        if (starting) PrepareNext(user, parts[1], anime, episode);
        return [source.Source];
    }

    // AniList hands out 30 titles a call; a folder shows three calls' worth, loaded together.
    private const int PagesPerFolder = 3;

    private sealed record Prepared(MediaSourceInfo Source, string Provider);
    // The next episode, looked up and probed while one plays: a site's video host can take half a minute to send
    // the first bytes Jellyfin needs before it starts. Per user, kept half an hour, one prepared at a time.
    private static readonly MemoryCache Ready = new(new MemoryCacheOptions { SizeLimit = 200 });
    private static readonly SemaphoreSlim Preparing = new(1, 1);
    public static bool PreparesNext => Plugin.Instance?.Configuration.PrepareNextEpisode != false;

    private async Task<Prepared> PlayAsync(Guid user, string scope, Anime anime, string episode, decimal? number, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (played, videos) = await sources.ResolveWithFallbackAsync(anime, number, episode, ct, listedExact: ListedExact(scope, anime.Id, episode));
        var provider = SourceRegistry.Split(played).Provider;
        var source = await relay.MediaSourceAsync(user, videos[0], $"{sources.Name(played)} · {videos[0].Quality} · English subtitles",
            token => sources.ResolveAsync(played, token), () => Plugin.Allows(user) && SourceRegistry.IsEnabled(provider) && AniListCatalog.IsAllowed(anime), ct);
        // Timed only when the chosen site played it: a fallback's time says little about either site.
        if (played == episode) sources.Health.Started(provider, clock.Elapsed);
        return new(source, provider);
    }

    private void PrepareNext(Guid user, string scope, Anime anime, string episode)
    {
        if (!PreparesNext) return;
        _ = Task.Run(async () =>
        {
            if (!await Preparing.WaitAsync(0)) return;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                if (await NextAsync(scope, anime, episode, limit.Token) is not { } next) return;
                var nextKey = SourceRegistry.Key(SourceRegistry.Split(episode).Provider, next.Id);
                var key = user.ToString("N") + $"episode/{scope}/{anime.Id}/{nextKey}";
                if (Ready.TryGetValue(key, out _)) return;
                var prepared = await PlayAsync(user, scope, anime, nextKey, next.Number, limit.Token);
                Ready.Set(key, prepared, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30) });
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException or IOException
                or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException)
            {
                logger.LogInformation("StreamFin anime: the next episode could not be got ready ({ErrorType})", ex.GetType().Name);
            }
            finally { Preparing.Release(); }
        }, CancellationToken.None);
    }

    // The episode after this one on the same site, from the listing the viewer saw.
    private async Task<SourceEpisode?> NextAsync(string scope, Anime anime, string episode, CancellationToken ct)
    {
        var provider = SourceRegistry.Split(episode).Provider;
        var prefix = $"source/{scope}/{anime.Id}/";
        var series = library.GetNewItemId($"title/{scope}/{anime.Id}Anime16", typeof(Series));
        foreach (var folder in library.GetItemList(new InternalItemsQuery { ParentId = series, Recursive = false }).OfType<Season>()
                     .Where(s => s.ExternalId.StartsWith(prefix, StringComparison.Ordinal) && SourceRegistry.Split(s.ExternalId[prefix.Length..]).Provider == provider))
        {
            var list = await sources.EpisodesAsync(folder.ExternalId[prefix.Length..], anime, ct);
            var at = list.ToList().FindIndex(e => SourceRegistry.Key(provider, e.Id) == episode);
            if (at >= 0) return at + 1 < list.Count ? list[at + 1] : null;
        }
        return null;
    }

    // The number the listing gave the episode ("12 · Title", or "12.5 · Title" for a recap), for finding it elsewhere.
    private decimal? EpisodeNumber(string id)
    {
        if (library.GetItemById(library.GetNewItemId(id + "Anime16", typeof(Episode))) is not Episode saved) return null;
        return Number(saved.Name) ?? saved.IndexNumber;
    }

    // What the title's listing said about the chosen site, without asking it (it may be the one that is down): one
    // folder named "Episodes" means its only entry was exactly this title; other folders are specials, other seasons
    // or loose matches. Null when the listing is not known.
    private bool? ListedExact(string scope, int animeId, string episode)
    {
        var provider = SourceRegistry.Split(episode).Provider;
        var series = library.GetNewItemId($"title/{scope}/{animeId}Anime16", typeof(Series));
        var prefix = $"source/{scope}/{animeId}/";
        var folders = library.GetItemList(new InternalItemsQuery { ParentId = series, Recursive = false }).OfType<Season>()
            .Where(s => s.ExternalId.StartsWith(prefix, StringComparison.Ordinal) && SourceRegistry.Split(s.ExternalId[prefix.Length..]).Provider == provider).ToArray();
        if (folders.Length == 0) return null;
        return folders.Length == 1 && folders[0].Name.StartsWith("Episodes · ", StringComparison.Ordinal);
    }

    public static decimal? Number(string? name) => name?.Split('·')[0].Trim() is { Length: > 0 } label
        && decimal.TryParse(label, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
}
