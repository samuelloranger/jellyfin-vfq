using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Registers plugin services: the middleware (universal, pre-playback) and
/// the PlaybackStart handler (fallback for edge cases).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost appHost)
    {
        // Primary: middleware intercepts PlaybackInfo responses for ALL clients.
        serviceCollection.AddSingleton<IStartupFilter, VfqStartupFilter>();

        // Fallback: hosted service sends SetAudioStreamIndex on PlaybackStart.
        serviceCollection.AddHostedService<VfqAudioSelectorService>();
    }
}
