using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ShareLinks.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
/// <remarks>
/// One setting so far. The milestone that defines the next one adds it here
/// together with the control that edits it and the validation that bounds it; a
/// setting that means nothing is a setting somebody eventually wires to
/// something.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the URL this server is reached at from outside, with no
    /// trailing slash, for example <c>https://media.example.org</c> or
    /// <c>https://example.org/jellyfin</c> when a proxy mounts it under a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty means the link is built from what the request claimed, which is text
    /// a caller supplies. <see cref="ShareLinkBuilder"/> is where that is argued
    /// and where the fallback lives.
    /// </para>
    /// <para>
    /// A string rather than a <see cref="System.Uri"/>, because the server writes
    /// this class out with <c>XmlSerializer</c> and that serialiser refuses a type
    /// with no parameterless constructor. What the setting has to survive is the
    /// round trip, and <c>PluginConfigurationTests</c> is where it does.
    /// </para>
    /// </remarks>
    public string PublicBaseUrl { get; set; } = string.Empty;
}
