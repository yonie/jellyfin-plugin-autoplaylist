using Jellyfin.Plugin.AutoPlaylist.Curation;
using Jellyfin.Plugin.AutoPlaylist.Ollama;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutoPlaylist;

/// <summary>
/// Registers the plugin's services with the server's container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient();
        serviceCollection.AddSingleton<RunLog>();
        serviceCollection.AddSingleton<OllamaClient>();
        serviceCollection.AddSingleton<PlaylistCurator>();
    }
}
