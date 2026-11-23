using Microsoft.Extensions.Caching.Memory;
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
public sealed class FilmChannel(TmdbCatalog catalog, FilmVersions versions, IHttpContextAccessor contexts,
    IAuthorizationContext authorization, ILibraryManager library, ILogger<FilmChannel> logger)
    : IChannel, IHasCacheKey, IRequiresMediaInfoCallback
{
    public const string ChannelName = "Movies & TV";
    public static bool IsAvailable => Plugin.Instance?.Configuration is { Enabled: true, FilmEnabled: true } && TmdbCatalog.HasKey;
    public string Name => ChannelName;
    public string Description => "StreamFin · Browse movies and TV and stream them online.";
    public string DataVersion => "streamfin-film-3-" + IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
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

    // What TMDB says about a title: rating, genres and premiere from any listing; age rating, studios or networks, cast
    // and directors, the IMDb id, backdrop and logo once its details are known.
    public static void Describe(Film film, FilmDetails? details, ChannelItemInfo item)
    {
        if (TmdbCatalog.Rating(film) is { } rating) item.CommunityRating = rating;
        var genres = details is { Genres.Length: > 0 } ? details.Genres : TmdbCatalog.Genres(film);
        if (genres.Count > 0) item.Genres = [.. genres];
        if (film.Premiere is { } premiere) item.PremiereDate = premiere;
        item.ProviderIds ??= [];
        if (details is null)
        {
            if (film.Backdrop is { } wide) ArtworkStore.Set(item.Id, new Artwork(Backdrop: wide));
            return;
        }
        if (details.Studios.Length > 0) item.Studios = [.. details.Studios];
        if (details.Certification is { } certification) item.OfficialRating = certification;
        if (details.ImdbId is { } imdb) item.ProviderIds["Imdb"] = imdb;
        // Each person once: someone who acts and directs is listed as director.
        var directors = details.Directors.Select(p => new PersonInfo { Name = p.Name, Role = p.Role, Type = p.Role == "Creator" ? Jellyfin.Data.Enums.PersonKind.Creator : Jellyfin.Data.Enums.PersonKind.Director, ImageUrl = p.Photo }).ToList();
        item.People = [.. directors, .. details.Cast.Where(p => !directors.Any(d => d.Name == p.Name))
            .Select(p => new PersonInfo { Name = p.Name, Role = p.Role, Type = Jellyfin.Data.Enums.PersonKind.Actor, ImageUrl = p.Photo })];
        ArtworkStore.Set(item.Id, new Artwork(Backdrop: details.Backdrop ?? film.Backdrop, Logo: details.Logo));
    }

    public ChannelItemInfo Described(Film film, string scope)
    {
        var item = Item(film, scope);
        Describe(film, catalog.KnownDetails(film), item);
        return item;
    }

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
        try
        {
            var result = await BrowseAsync(id, ct);
            // Saved titles get what the listing now knows, in the background.
            ChannelMetadata.InBackground(token => ChannelMetadata.SyncAsync(library, Name, result.Items, token), logger);
            return result;
        }
        catch (Exception ex) when (ex is ProviderException or HttpRequestException or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
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
            // TMDB hands out 20 titles a call; a folder shows five calls' worth, loaded together.
            var data = await Pages.LoadAsync(page, 5, 500, number => catalog.BrowseAsync(category, number, ct), d => d.Items, d => d.HasNext, f => (f.Kind, f.Id));
            var items = data.Items.Select((film, index) => { var item = Described(film, category); item.IndexNumber = index + 1; return item; }).ToList();
            catalog.PrefetchDetails(data.Items);
            if (data.Next is { } next) items.Add(Folder($"list/{category}/{next}", $"More ({(next - 1) / 5 + 1})"));
            return Result(items);
        }
        if (parts is ["tv", var scope, var showText] && int.TryParse(showText, out var showId))
        {
            var show = await catalog.GetAsync(FilmKind.Tv, showId, ct);
            ChannelMetadata.InBackground(token => DescribeShowAsync(show, scope, token), logger);
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
            var genres = catalog.KnownDetails(show) is { Genres.Length: > 0 } known ? known.Genres : TmdbCatalog.Genres(show).ToArray();
            return Result((await catalog.EpisodesAsync(seriesId, seasonNumber, ct)).Select(episode => new ChannelItemInfo
            {
                Id = $"ep/{seasonScope}/{seriesId}/{seasonNumber}/{episode.Number}",
                Name = episode.Name, SeriesName = show.Title, Type = ChannelItemType.Media, MediaType = ChannelMediaType.Video,
                ContentType = ChannelMediaContentType.Episode, ImageUrl = episode.Image ?? show.Image, Overview = episode.Overview,
                IndexNumber = episode.Number, ParentIndexNumber = seasonNumber, ProductionYear = episode.Aired?.Year, PremiereDate = episode.Aired,
                RunTimeTicks = (episode.Runtime ?? show.Runtime) is int minutes && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : null,
                Tags = ["StreamFin"], ProviderIds = new Dictionary<string, string> { ["Custom"] = $"streamfin-ep-{seriesId}-{seasonNumber}-{episode.Number}" },
                Genres = [.. genres]
            }));
        }
        return Result([]);
    }

    // Opening a show fetches its details in the background and gives its saved series item cast, studios and pictures
    // (the series was listed before they were known); they show the next time its page opens.
    private async Task DescribeShowAsync(Film show, string scope, CancellationToken ct)
    {
        var details = await catalog.DetailsAsync(show, ct);
        var info = Item(show, scope);
        Describe(show, details, info);
        if (library.GetItemById(library.GetNewItemId(info.Id + Name + "16", typeof(Series))) is { } series)
            await ChannelMetadata.ApplyAsync(library, series, info, ct);
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken ct)
    {
        var context = contexts.HttpContext;
        if (context is null) return [];
        var auth = await authorization.GetAuthorizationInfo(context);
        if (!auth.IsAuthenticated || !Plugin.Allows(auth.UserId) || !IsAvailable) throw new UnauthorizedAccessException("StreamFin is not enabled for this account.");
        var target = await versions.TargetAsync(id, ct);
        var user = auth.UserId;
        var type = id.StartsWith("movie/", StringComparison.Ordinal) ? typeof(Movie) : typeof(Episode);
        // Only a real start prepares the next episode, not opening an episode's page.
        var starting = context.Request.Path.Value?.Contains("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) == true;
        // A version picked on the page arrives as its id; it is probed now if it was only listed so far.
        var picked = context.Items[FilmVersions.PickedKey] as string is { Length: > 0 } fromFilter ? fromFilter : context.Request.Query["mediaSourceId"].ToString();
        var found = await versions.PlayAsync(user, id, picked, ct);
        if (starting)
        {
            PrepareNext(user, id, target.Film, target.Season, target.Episode);
            versions.ProbeOthers(found);
        }
        await ResumeRuntime.KeepAsync(library, id + ChannelName + "16", type, found.Probed?.RunTimeTicks, ct);
        return found.List;
    }

    private static readonly SemaphoreSlim Preparing = new(1, 1);

    private void PrepareNext(Guid user, string id, Film film, int? season, int? episode)
    {
        if (!AnimeChannel.PreparesNext || season is not { } s || episode is not { } e || id.Split('/') is not ["ep", var scope, var show, _, _]) return;
        _ = Task.Run(async () =>
        {
            if (!await Preparing.WaitAsync(0)) return;
            using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                // The next in this season, else the first of the next season.
                var (nextSeason, nextEpisode) = (await catalog.EpisodesAsync(film.Id, s, limit.Token)).Any(x => x.Number == e + 1) ? (s, e + 1)
                    : (await catalog.SeasonsAsync(film.Id, limit.Token)).Any(x => x.Number == s + 1) ? (s + 1, 1) : (0, 0);
                if (nextSeason == 0) return;
                await versions.PrepareAsync(user, $"ep/{scope}/{show}/{nextSeason}/{nextEpisode}", limit.Token);
            }
            catch (Exception ex) when (ex is ProviderException or HttpRequestException or OperationCanceledException or IOException
                or System.Text.RegularExpressions.RegexMatchTimeoutException or UriFormatException or System.Text.Json.JsonException)
            {
                logger.LogInformation("StreamFin films: the next episode could not be got ready ({ErrorType})", ex.GetType().Name);
            }
            finally { Preparing.Release(); }
        }, CancellationToken.None);
    }
}
