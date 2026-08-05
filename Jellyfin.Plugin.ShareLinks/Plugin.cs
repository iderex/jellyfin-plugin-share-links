using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ShareLinks.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Share Links";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a3703f07-f83d-49a0-a09f-50b890a2baac");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Near-miss fixture. InternalItemsQuery.UseRawName was added after the
    /// declared floor and exists in the development version, so this compiles
    /// against one and not the other.
    /// </summary>
    /// <returns>Nothing anybody uses.</returns>
    public static bool? NearMiss() => new MediaBrowser.Controller.Entities.InternalItemsQuery { UseRawName = true }.UseRawName;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
