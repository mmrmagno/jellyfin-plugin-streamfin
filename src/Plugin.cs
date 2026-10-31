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
    public bool HideAdultContent { get; set; } = true;
    public bool HideEcchiContent { get; set; } = true;
    public int MetadataCacheMinutes { get; set; } = 60;
    public bool FilmEnabled { get; set; } = true;
    public string TmdbApiKey { get; set; } = "";
    public bool MusicEnabled { get; set; } = true;
    // Empty means MusicCatalog.DefaultApi.
    public string MusicApiUrl { get; set; } = "";
    public bool MusicPreferExplicit { get; set; } = true;
    // Deezer's public catalog answers searches, albums and artists while the music catalog cannot.
    public bool MusicDeezer { get; set; } = true;
    // Songs kept on disk so skips and replays do not go back to the catalog; 0 turns it off.
    public int MusicCacheMb { get; set; } = 4096;
    // When no FLAC source answers, a song may play from a lossy one (YouTube's AAC) instead of failing.
    public bool MusicAllowLossy { get; set; } = true;
    // YouTube needs yt-dlp and deno on the server; this setting lets the plugin fetch and update them.
    public bool MusicYouTube { get; set; }
    // Empty means the copies the plugin keeps in its own data folder.
    public string YtDlpPath { get; set; } = "";
    public string DenoPath { get; set; } = "";
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
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "streamfin", EmbeddedResourcePath = "StreamFin.Configuration.html" }];
    public static bool Allows(Guid id) => id != Guid.Empty && Instance?.Configuration is { Enabled: true } c
        && c.AllowedUserIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == id);
}
