using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace StreamFin;

public sealed class ServiceRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<SafeHttp>();
        services.AddSingleton<AniListCatalog>();
        services.AddSingleton<TmdbCatalog>();
        services.AddSingleton<CatalogWarmer>();
        services.AddSingleton<FlixSource>();
        services.AddSingleton<VidlySource>();
        services.AddSingleton<IFilmSource>(provider => provider.GetRequiredService<FlixSource>());
        services.AddSingleton<IFilmSource>(provider => provider.GetRequiredService<VidlySource>());
        services.AddSingleton<IFilmSource, AstraSource>();
        services.AddSingleton<IFilmSource, VidBoltSource>();
        services.AddSingleton<IFilmSource, PunkflixSource>();
        services.AddSingleton<FilmSources>();
        services.AddSingleton<MusicMetadataStore>();
        services.AddSingleton(provider => new MonochromeHealth(provider.GetService<Microsoft.Extensions.Logging.ILogger<MonochromeHealth>>()));
        services.AddSingleton<VideoHealth>();
        services.AddSingleton<VideoChecks>();
        services.AddSingleton<VideoProbe>();
        services.AddHostedService(provider => provider.GetRequiredService<VideoProbe>());
        services.AddSingleton<MonochromeProbe>();
        services.AddHostedService(provider => provider.GetRequiredService<MonochromeProbe>());
        services.AddSingleton<MusicCatalog>();
        services.AddSingleton<MusicLyrics>();
        services.AddSingleton<MusicRadio>();
        services.AddSingleton<MusicMix>();
        services.AddSingleton<MusicCache>();
        services.AddSingleton<YtDlp>();
        services.AddSingleton<MusicAudio>();
        services.AddSingleton<MusicPrefetch>();
        services.AddSingleton<MusicArtCache>();
        services.AddSingleton<SubsonicAuth>();
        services.AddSingleton(_ => new MusicLibraryStore());
        services.AddSingleton<SpotifyImport>();
        services.AddSingleton<MusicSaver>();
        services.AddHostedService(provider => provider.GetRequiredService<MusicSaver>());
        services.AddSingleton<IAnimeSource, HiAnimeSource>();
        services.AddSingleton<IAnimeSource, AnimeGgSource>();
        services.AddSingleton<IAnimeSource, AnimeHeavenSource>();
        services.AddSingleton<SourceRegistry>();
        services.AddSingleton<StreamRelay>();
        services.AddSingleton<IChannel, AnimeChannel>();
        services.AddSingleton<IChannel, FilmChannel>();
        services.AddSingleton<IChannel, MusicChannel>();
        services.AddSingleton<IChannel, HubChannel>();
        services.AddScoped<StreamingNavigation>();
        services.AddScoped<LibraryStreamingLinks>();
        services.AddSingleton<SearchHistory>();
        services.AddSingleton<OnlineSearch>();
        services.AddSingleton<FilmSearch>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider>(provider => provider.GetRequiredService<OnlineSearch>());
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider>(provider => provider.GetRequiredService<FilmSearch>());
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, MusicSearch>();
        services.AddScoped<ContentAccess>();
        services.AddScoped<ChannelAccessFilter>();
        services.AddScoped<ArtworkAccess>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ChannelAccessFilter>());
    }
}
