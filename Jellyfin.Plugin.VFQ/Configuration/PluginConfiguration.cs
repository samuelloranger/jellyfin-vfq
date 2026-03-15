using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.VFQ.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EnableAutoSelect = true;
        PreferHighestQuality = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether automatic VFQ selection is enabled.
    /// </summary>
    public bool EnableAutoSelect { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to prefer the highest quality VFQ track
    /// (most channels, best codec) when multiple VFQ tracks exist.
    /// </summary>
    public bool PreferHighestQuality { get; set; }
}
