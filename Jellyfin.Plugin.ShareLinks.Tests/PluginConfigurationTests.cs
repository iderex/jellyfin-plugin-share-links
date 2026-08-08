using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.ShareLinks.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// The plugin template ships four demonstration settings and an enum for one of
/// them, and the configuration page renders all four. None of them means anything
/// here. These tests refuse them coming back by accident, and refuse a
/// configuration page that is declared but not shipped.
/// </summary>
public class PluginConfigurationTests
{
    private static readonly Assembly PluginAssembly = typeof(PluginConfiguration).Assembly;

    [Fact]
    public void ConfigurationDeclaresOnlyTheSettingsThatHaveLanded()
    {
        // DeclaredOnly, because BasePluginConfiguration brings its own and those are
        // not this repository's to remove.
        //
        // The set is exact rather than a check that each template setting is absent.
        // Naming the four would be a list to keep in step with a template this
        // repository no longer follows; an exact set refuses them, refuses a fifth
        // nobody meant to add, and makes the line somebody edits the line that says
        // which settings exist.
        var declared = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["PublicBaseUrl"], declared);
    }

    [Fact]
    public void TheBaseUrlSettingSurvivesTheSerialiserTheServerUses()
    {
        // The server writes this class out with XmlSerializer and reads it back on
        // the next start, so a setting that does not round-trip is a setting an
        // operator sets once and finds empty after a restart. share-store.md quotes
        // the base class member that does it; this is the property that member needs
        // to hold.
        var serialiser = new XmlSerializer(typeof(PluginConfiguration));
        var written = new StringWriter(CultureInfo.InvariantCulture);
        serialiser.Serialize(written, new PluginConfiguration { PublicBaseUrl = "https://media.example.org" });

        using var read = new StringReader(written.ToString());
        var restored = Assert.IsType<PluginConfiguration>(serialiser.Deserialize(read));

        Assert.Equal("https://media.example.org", restored.PublicBaseUrl);
    }

    [Fact]
    public void TheTemplateOptionsEnumIsGone()
    {
        var leftovers = PluginAssembly.GetTypes()
            .Where(type => type.Name.Equals("SomeOptions", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(leftovers);
    }

    [Fact]
    public void TheConfigurationPageIsEmbeddedUnderTheNameThePluginAsksFor()
    {
        // Plugin.GetPages builds this name from its own namespace at run time. A page
        // that is renamed or dropped from the project leaves the dashboard asking the
        // server for a resource that is not there.
        var expected = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.Configuration.configPage.html",
            typeof(Plugin).Namespace);

        Assert.Contains(expected, PluginAssembly.GetManifestResourceNames(), StringComparer.Ordinal);
    }

    [Fact]
    public void TheConfigurationPageNamesNoPluginIdentifier()
    {
        // The template page carried the template's guid in a script literal, a third
        // copy of the identifier that nothing kept in step with the other two. The
        // page has no script and needs no identifier; if one ever returns it has to
        // come from a source that cannot drift.
        using var stream = PluginAssembly.GetManifestResourceStream(
            string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", typeof(Plugin).Namespace));
        Assert.NotNull(stream);

        using var reader = new StreamReader(stream);
        var page = reader.ReadToEnd();

        Assert.DoesNotContain("eb5d7894-8eef-4b36-aa6f-5d124e828ce1", page, StringComparison.OrdinalIgnoreCase);
    }
}
