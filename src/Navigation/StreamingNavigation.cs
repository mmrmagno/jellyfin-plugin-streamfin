using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace StreamFin;

// One StreamFin entry with Anime, Movies & TV and Music inside. It holds navigation only: API results swap each of its
// folders for the real channel (or one of its folders), so browsing, watch progress and playback stay on the original
// channels whichever layout is chosen. Adapted from ItzDabbzz's fork (GPL-2.0).
public sealed class HubChannel : IChannel, IHasCacheKey
{
    // Jellyfin derives the channel's id from its name, so this never changes.
    public const string ChannelName = "StreamFin";
    public string Name => ChannelName;
    public string Description => "StreamFin · Anime, movies, TV and music from online sources.";
    public string DataVersion => "streamfin-hub-2-" + StreamingNavigation.Mode + "-" + SourceRegistry.AnyEnabled + "-" + FilmChannel.IsAvailable
        + "-" + MusicChannel.IsAvailable + "-" + (Plugin.Instance?.Configuration.CacheRevision ?? 0);
    public string HomePageUrl => "https://github.com/mmrmagno/jellyfin-plugin-streamfin";
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;
    public static bool Enabled => Plugin.Instance?.Configuration is { Enabled: true } && StreamingNavigation.Mode is "Hub" or "Both";
    public bool IsEnabledFor(string userId) => Enabled && Guid.TryParse(userId, out var id) && Plugin.Allows(id);
    public string GetCacheKey(string? userId) => DataVersion + "-" + userId;
    public InternalChannelFeatures GetChannelFeatures() => new()
    {
        MediaTypes = [ChannelMediaType.Video, ChannelMediaType.Audio],
        ContentTypes = [ChannelMediaContentType.Movie, ChannelMediaContentType.Episode, ChannelMediaContentType.Song],
        AutoRefreshLevels = 0, SupportsContentDownloading = false
    };
    public IEnumerable<ImageType> GetSupportedChannelImages() => [ImageType.Primary];
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken ct) => Task.FromResult(new DynamicImageResponse
    {
        HasImage = type == ImageType.Primary, Format = ImageFormat.Png,
        Stream = typeof(HubChannel).Assembly.GetManifestResourceStream("StreamFin.Assets.streamfin.png")
    });

    // The folders the hub shows, in order, for the channels that are available now.
    public static IEnumerable<(string Key, string Name)> Folders() =>
        new[] { ("anime", "Anime", SourceRegistry.AnyEnabled), ("films", "Movies & TV", FilmChannel.IsAvailable), ("music", "Music", MusicChannel.IsAvailable) }
            .Where(f => f.Item3).Select(f => (f.Item1, f.Item2));

    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.UserId != Guid.Empty && !Plugin.Allows(query.UserId)) throw new UnauthorizedAccessException();
        var items = Enabled && string.IsNullOrEmpty(query.FolderId)
            ? Folders().Select(f => new ChannelItemInfo { Id = f.Key, Name = f.Name, Type = ChannelItemType.Folder, FolderType = ChannelFolderType.Container, Tags = ["StreamFin"] }).ToList()
            : [];
        return Task.FromResult(new ChannelItemResult { Items = items, TotalRecordCount = items.Count });
    }
}

public sealed class StreamingNavigation(ILibraryManager library, IChannelManager channels, IDtoService dtos)
{
    public static string Mode => NormalizeMode(Plugin.Instance?.Configuration.StreamingNavigationMode);
    public static string NormalizeMode(string? value) => value switch { "Hub" => "Hub", "Both" => "Both", _ => "Channels" };

    // Where a hub folder or a library link leads: a channel, optionally one of its catalog folders, and the name shown.
    public static (string Channel, string? Folder, string Name)? Target(string? key) => key switch
    {
        "anime" => ("Anime", null, "Anime"),
        "films" => (FilmChannel.ChannelName, null, "Movies & TV"),
        "movies" => (FilmChannel.ChannelName, "list/movies/1", "Popular movies"),
        "tv" => (FilmChannel.ChannelName, "list/tv/1", "Popular TV"),
        "music" => (MusicChannel.ChannelName, null, "Music"),
        _ => null
    };
    private static bool Available(string channel) =>
        channel == "Anime" ? SourceRegistry.AnyEnabled : channel == FilmChannel.ChannelName ? FilmChannel.IsAvailable : MusicChannel.IsAvailable;

    private Guid ChannelId(string name) => library.GetNewItemId("Channel " + name, typeof(Channel));
    public Guid HubId => ChannelId(HubChannel.ChannelName);
    public bool IsPlaceholder(Guid id) => id != Guid.Empty && id != HubId && library.GetItemById(id) is { } item && item.ChannelId == HubId;
    // In Hub mode the separate channels leave the menu and home screen; their items stay reachable through the hub.
    public bool HidesChannel(Guid id) => Mode == "Hub" && (id == ChannelId("Anime") || id == ChannelId(FilmChannel.ChannelName) || id == ChannelId(MusicChannel.ChannelName));

    // Swaps a hub folder for what it leads to. Null hides it (not allowed, or its channel is off).
    public async Task<BaseItemDto?> ResolveAsync(BaseItemDto dto, User? user, CancellationToken ct)
    {
        if (!IsPlaceholder(dto.Id)) return dto;
        if (user is null || !Plugin.Allows(user.Id) || !HubChannel.Enabled) return null;
        return await ResolveTargetAsync(library.GetItemById(dto.Id)?.ExternalId, user, ct);
    }

    public async Task<BaseItemDto?> ResolveTargetAsync(string? key, User user, CancellationToken ct)
    {
        if (!Plugin.Allows(user.Id) || Target(key) is not { } target || !Available(target.Channel)) return null;
        if (library.GetItemById(ChannelId(target.Channel)) is not Channel channel) return null;
        BaseItem? found = channel;
        if (target.Folder is not null)
        {
            await ChannelNavigation.Gate.WaitAsync(ct);
            try
            {
                var children = await channels.GetChannelItemsInternal(new InternalItemsQuery(user) { Parent = channel, ChannelIds = [channel.Id] }, new Progress<double>(), ct);
                found = children.Items.FirstOrDefault(item => item.ExternalId == target.Folder);
            }
            finally { ChannelNavigation.Gate.Release(); }
        }
        if (found is null) return null;
        var resolved = dtos.GetBaseItemDtos([found], new DtoOptions(true), user).FirstOrDefault();
        if (resolved is not null) resolved.Name = target.Name;
        return resolved;
    }
}
