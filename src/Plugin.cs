using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace AniFin;

public sealed class Configuration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public string[] AllowedUserIds { get; set; } = [];
    public bool AnimeGgEnabled { get; set; } = true;
    public int MetadataCacheMinutes { get; set; } = 60;
    // Incrementing this invalidates Jellyfin's own channel cache as well as ours.
    public int CacheRevision { get; set; }
}

public sealed class Plugin : BasePlugin<Configuration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public static readonly Guid PluginId = new("a74fdb2e-938f-483b-b2ef-f527e14ff724");
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override Guid Id => PluginId;
    public override string Name => "AniFin";
    public override string Description => "Browse anime and stream subtitled episodes from online sources.";
    public IEnumerable<PluginPageInfo> GetPages() => [new() { Name = "anifin", EmbeddedResourcePath = "AniFin.Configuration.html" }];
    public static bool Allows(Guid id) => id != Guid.Empty && Instance?.Configuration is { Enabled: true } c
        && c.AllowedUserIds.Any(value => Guid.TryParse(value, out var allowed) && allowed == id);
}
