using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.VFQ;

/// <summary>
/// Startup filter that injects the VFQ PlaybackInfo middleware into the ASP.NET pipeline.
/// </summary>
public class VfqStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<VfqPlaybackInfoMiddleware>();
            next(app);
        };
    }
}
