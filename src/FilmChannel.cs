using System.Globalization;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StreamFin;

// Item IDs: movie/<scope>/<tmdb>, tv/<scope>/<tmdb>, season/<scope>/<tmdb>/<n>, ep/<scope>/<tmdb>/<s>/<e>.
// The scope (listing name) keeps one title from being moved between folders by Jellyfin;
// the TMDB provider ID lets watch progress follow the title across listings.
public sealed class FilmChannel(TmdbCatalog catalog, FlixSource flix, StreamRelay relay, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILibraryManager library, ILogger<FilmChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Movies & TV";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, FilmEnabled: true } && TmdbCatalog.HasKey;
    public string Name => ChannelName;
    public string Description => "StreamFin · Browse movies and TV and stream them online.";
    public string DataVersion => "streamfin-film-1-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public bool IsEnabledFor(string userId) => IsAvailable && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => $"{userId}-{DateTime.UtcNow.Ticks / TimeSpan.FromMinutes(Math.Clamp(Plugin.Instance?.Configuration.MetadataCacheMinutes ?? 60, 1, 1440)).Ticks}";
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video], ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(FilmChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.popcorn.png")
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
    public static Dictionary<string, string> Ids(Film film) => new() { ["Tmdb"] = film.Id.ToString(CultureInfo.InvariantCulture), ["Custom"] = $"streamfin-{film.Kind.ToString().ToLowerInvariant()}-{film.Id}" };

    public static ChannelItemInfo Item(Film film, string scope)
    {
        if (film.Kind == FilmKind.Movie)
            return new ChannelItemInfo
            {
                Id = $"movie/{scope}/{film.Id}", Name = film.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Movie, ImageUrl = film.Image, Overview = film.Overview, ProductionYear = film.Year,
                RunTimeTicks = film.Runtime is > 0 ? TimeSpan.FromMinutes(film.Runtime.Value).Ticks : null, Tags = ["StreamFin"], ProviderIds = Ids(film)
            };
        var item = Folder($"tv/{scope}/{film.Id}", film.Title, film.Overview);
        item.FolderType = ChannelFolderType.Series;
        item.ImageUrl = film.Image;
        item.ProductionYear = film.Year;
        item.ProviderIds = Ids(film);
        return item;
    }

    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        if (!IsAvailable) return Result([]);
        var id = query.FolderId ?? "";
        try { return await BrowseAsync(id, ct); }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("StreamFin film catalog request failed ({ErrorType})", ex.GetType().Name);
            var message = ex is ProviderException ? ex.Message : "The catalog is temporarily unavailable. Please try again later.";
            return Result([Folder(id + "/status", message, message)]);
        }
    }

    private async Task<ChannelItemResult> BrowseAsync(string id, CancellationToken ct)
    {
        if (id.EndsWith("/status", StringComparison.Ordinal)) return Result([]);
        if (id.Length == 0) return Result([
            Folder("search", "Search results", "Use Jellyfin's search (magnifying glass) to find movies and shows online. Your discovered titles appear here."),
            Folder("list/movies/1", "Popular movies"), Folder("list/tv/1", "Popular TV"), Folder("list/trending/1", "Trending this week")]);
        var parts = id.Split('/');
        if (id == "search")
        {
            var parent = library.GetNewItemId(FilmSearch.FolderKey, typeof(Folder));
            return Result(library.GetItemList(new InternalItemsQuery { ParentId = parent, Recursive = false }).Select(item => item switch
            {
                Movie movie => new ChannelItemInfo
                {
                    Id = movie.ExternalId, Name = movie.Name, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                    ContentType = ChannelMediaContentType.Movie, Overview = movie.Overview, ProductionYear = movie.ProductionYear,
                    RunTimeTicks = movie.RunTimeTicks, Tags = movie.Tags.ToList(), ProviderIds = movie.ProviderIds
                },
                _ => new ChannelItemInfo
                {
                    Id = item.ExternalId, Name = item.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Series,
                    Overview = item.Overview, ProductionYear = item.ProductionYear, Tags = item.Tags.ToList(), ProviderIds = item.ProviderIds
                }
            }));
        }
        if (parts is ["list", var category, var pageText] && int.TryParse(pageText, out var page))
        {
            var data = await catalog.BrowseAsync(category, page, ct);
            var items = data.Items.Select((film, index) => { var item = Item(film, category); item.IndexNumber = index + 1; return item; }).ToList();
            if (data.HasNext && page < 500) items.Add(Folder($"list/{category}/{page + 1}", $"Next page ({page + 1})"));
            return Result(items);
        }
        if (parts is ["tv", var scope, var showText] && int.TryParse(showText, out var showId))
        {
            var show = await catalog.GetAsync(FilmKind.Tv, showId, ct);
            return Result((await catalog.SeasonsAsync(showId, ct)).Select(season =>
            {
                var item = Folder($"season/{scope}/{showId}/{season.Number}", season.Name, $"{season.Episodes} episodes");
                item.FolderType = ChannelFolderType.Season;
                item.IndexNumber = season.Number;
                item.ImageUrl = season.Image ?? show.Image;
                return item;
            }));
        }
        if (parts is ["season", var seasonScope, var seriesText, var seasonText] && int.TryParse(seriesText, out var seriesId) && int.TryParse(seasonText, out var seasonNumber))
        {
            var show = await catalog.GetAsync(FilmKind.Tv, seriesId, ct);
            return Result((await catalog.EpisodesAsync(seriesId, seasonNumber, ct)).Select(episode => new ChannelItemInfo
            {
                Id = $"ep/{seasonScope}/{seriesId}/{seasonNumber}/{episode.Number}",
                Name = episode.Name, SeriesName = show.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode, ImageUrl = episode.Image ?? show.Image, Overview = episode.Overview,
                IndexNumber = episode.Number, ParentIndexNumber = seasonNumber, ProductionYear = episode.Aired?.Year, PremiereDate = episode.Aired,
                RunTimeTicks = (episode.Runtime ?? show.Runtime) is int minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : null,
                Tags = ["StreamFin"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"streamfin-ep-{seriesId}-{seasonNumber}-{episode.Number}" }
            }));
        }
        return Result([]);
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        (Film Film, int? Season, int? Episode) target = id.Split('/') switch
        {
            ["movie", _, var movie] when int.TryParse(movie, out var movieId) => (await catalog.GetAsync(FilmKind.Movie, movieId, ct), null, null),
            ["ep", _, var show, var s, var e] when int.TryParse(show, out var showId) && int.TryParse(s, out var season) && int.TryParse(e, out var episode)
                && season is > 0 and < 1000 && episode is > 0 and < 10000 => (await catalog.GetAsync(FilmKind.Tv, showId, ct), season, episode),
            _ => throw new ProviderException("Invalid StreamFin title.")
        };
        var videos = await flix.ResolveAsync(target.Film, target.Season, target.Episode, ct);
        var user = auth.UserId;
        var subtitles = videos[0].Subtitles?.Count ?? 0;
        return [await relay.MediaSourceAsync(user, videos[0], $"FlixHQ · {videos[0].Quality}" + (subtitles > 0 ? $" · {subtitles} subtitle tracks" : ""),
            token => flix.ResolveAsync(target.Film, target.Season, target.Episode, token), () => Plugin.Allows(user) && IsAvailable, ct)];
    }
}
