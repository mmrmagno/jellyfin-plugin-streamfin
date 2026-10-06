using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AniFin;

public sealed class ServiceRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<SafeHttp>();
        services.AddSingleton<AniListCatalog>();
        services.AddSingleton<IAnimeSource, AnimeGgSource>();
        services.AddSingleton<StreamRelay>();
        services.AddSingleton<IChannel, AnimeChannel>();
        services.AddSingleton<MediaBrowser.Controller.Library.ISearchProvider, OnlineSearch>();
        services.AddScoped<ContentAccess>();
        services.AddScoped<ChannelAccessFilter>();
        services.AddScoped<ArtworkAccess>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<ChannelAccessFilter>());
    }
}
