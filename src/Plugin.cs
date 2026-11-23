using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace StreamFin;

public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string[] AllowedUserIds { get; set; } = [];
    // HiAnime. The name and the "zoko" source id are kept so saved settings and episode IDs survive.
    public bool ZokoEnabled { get; set; } = true;
    public bool AnimeGgEnabled { get; set; } = true;
    public bool AnimeHeavenEnabled { get; set; } = true;
    public bool AnimeProviderFallback { get; set; } = true;
    public bool PrepareNextEpisode { get; set; } = true;
    public bool HideAdultContent { get; set; } = true;
    public bool HideEcchiContent { get; set; } = true;
    public int MetadataCacheMinutes { get; set; } = 60;
    public bool FilmEnabled { get; set; } = true;
    // Fallback Movies & TV sources, asked in order after FlixHQ (FilmSources.Order) for titles it does not have or cannot play.
    public bool FilmVidly { get; set; } = true;
    public bool FilmAstra { get; set; } = true;
    public bool FilmVidBolt { get; set; } = true;
    public bool FilmPunkflix { get; set; } = true;
    public bool FilmVidRift { get; set; } = true;
    public bool FilmRive { get; set; } = true;
    // Movies & TV quality: the tallest picture allowed (0 = no limit), and H.264 first at equal quality so the server
    // need not convert the video for most players.
    public int FilmMaxHeight { get; set; }
    public bool FilmPreferH264 { get; set; } = true;
    public string TmdbApiKey { get; set; } = "";
    public bool MusicEnabled { get; set; } = true;
    // Empty means MusicCatalog.DefaultApi.
    public string MusicApiUrl { get; set; } = "";
    public bool MusicPreferExplicit { get; set; } = true;
    // Off: online titles stop appearing in Jellyfin search. The channels are not affected.
    public bool OnlineSearch { get; set; } = true;
    // Channels: Anime, Movies & TV and Music each have their own entry (as before). Hub: one StreamFin entry holds them.
    // Both: the StreamFin entry and the separate channels.
    public string StreamingNavigationMode { get; set; } = "Channels";
    // A StreamFin entry at the top of these Jellyfin libraries (category: anime, movies, tv or music).
    public List<StreamingLibraryLink> StreamingLibraryLinks { get; set; } = [];
    // Off: no song plays from Monochrome's audio host; its catalog still answers searches, albums and artists.
    public bool MusicMonochrome { get; set; } = true;
    // Where songs play from and where searches go first: the first that works wins. Unknown names are dropped and
    // missing ones go last, so a source added in a later version joins at the end.
    public string[] MusicPlayOrder { get; set; } = ["monochrome", "youtube"];
    public string[] MusicSearchOrder { get; set; } = ["monochrome", "deezer"];
    // While a song plays, the next one is fetched into the song cache.
    public bool MusicPrefetch { get; set; } = true;
    // Deezer's public catalog answers searches, albums and artists while the music catalog cannot.
    public bool MusicDeezer { get; set; } = true;
    // Playlist import for every allowed user (the import page, and links pasted into search). Spotify links are read
    // from Spotify's public embed page, which is not an official API, so they have a switch of their own.
    public bool PlaylistImport { get; set; } = true;
    public bool ImportSpotifyLinks { get; set; } = true;
    // Songs kept on disk so skips and replays do not go back to the catalog; 0 turns it off.
    public int MusicCacheMb { get; set; } = 4096;
    // When no FLAC source answers, a song may play from a lossy one (YouTube's AAC) instead of failing.
    public bool MusicAllowLossy { get; set; } = true;
    // YouTube needs yt-dlp and deno on the server; this setting lets the plugin fetch and update them.
    public bool MusicYouTube { get; set; }
    // Empty means the copies the plugin keeps in its own data folder.
    public string YtDlpPath { get; set; } = "";
    public string DenoPath { get; set; } = "";
    public string YouTubeProxy { get; set; } = "";
    // Starred albums are saved here as FLAC files for a normal music library; empty turns it off.
    public string MusicLibraryFolder { get; set; } = "";
    // Optional: Last.fm's similar artists when ListenBrainz has none.
    public string LastFmApiKey { get; set; } = "";
    public bool SubsonicEnabled { get; set; }
    public bool SubsonicPreferLossless { get; set; } = true;
    public SubsonicPassword[] SubsonicPasswords { get; set; } = [];
    // Incrementing this invalidates Jellyfin's own channel cache as well as ours.
    public int CacheRevision { get; set; }
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public static readonly Guid PluginId = new("a74fdb2e-938f-483b-b2ef-f527e14ff724");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override Guid Id => PluginId;
    public override string Name => "StreamFin";
    public override string Description => "Anime, movies, TV and lossless music from online sources, as Jellyfin channels.";
    // EnableInMainMenu puts StreamFin in the dashboard sidebar; without it the page is only reachable from the plugin card.
    public IEnumerable<PluginPageInfo> GetPages() => [new()
    {
        Name = "streamfin",
        DisplayName = "StreamFin",
        EmbeddedResourcePath = "StreamFin.Configuration.html",
        EnableInMainMenu = true,
        MenuSection = "server",
        MenuIcon = "extension"
    }];
    public static string[] Ordered(string[]? saved, params string[] known) => (saved ?? []).Where(s => known.Contains(s)).Concat(known).Distinct().ToArray();
    public static bool Allows(Guid id) => id != Guid.Empty && Instance?.Configuration is { Enabled: true } c
        && c.AllowedUserIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == id);
}
