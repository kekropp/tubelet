using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Tubelet;

/// <summary>
/// Registers the plugin's own services with the Jellyfin host container. Metadata, image and
/// segment providers plus the scheduled task are auto-discovered by Jellyfin; only the shared
/// <see cref="TubeletClient"/> needs explicit wiring.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<TubeletClient>();
    }
}
