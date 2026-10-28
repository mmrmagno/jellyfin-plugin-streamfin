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
        services.AddSingleton<FlixSource>();
        services.AddSingleton<MusicCatalog>();
        services.AddSingleton<MusicLyrics>();
        services.AddSingleton<MusicRadio>();
        services.AddSingleton<MusicMix>();
        services.AddSingleton<MusicCache>();
        services.AddSingleton<SubsonicAuth>();
        services.AddSingleton(_ => new MusicLibraryStore());
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
        services.AddSingleton<SearchHistory>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, OnlineSearch>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, FilmSearch>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, MusicSearch>();
        services.AddScoped<ContentAccess>();
        services.AddScoped<ChannelAccessFilter>();
        services.AddScoped<ArtworkAccess>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ChannelAccessFilter>());
    }
}
