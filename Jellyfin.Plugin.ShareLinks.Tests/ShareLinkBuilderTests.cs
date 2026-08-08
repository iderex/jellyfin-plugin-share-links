using System;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// The link an operator copies out of the dashboard is the whole product of this
/// plugin, and the host in it decides where the guest ends up. These tests hold
/// the one property that makes the link safe to forward: the host comes from the
/// operator's configuration, and a request that says otherwise is not believed
/// (#49).
/// </summary>
public class ShareLinkBuilderTests
{
    private const string Configured = "https://media.example.org";

    // What a forged Host header arrives as. A header is text the caller wrote, and
    // by the time the plugin sees it there is nothing left in it that says who
    // wrote it.
    private const string ForgedHost = "attacker.example.net";

    private const string Path = "/ShareLinks/Open/abc";

    [Fact]
    public void AForgedHostDoesNotReachTheLink()
    {
        var claimed = ShareLinkBuilder.FromRequestParts("https", ForgedHost, string.Empty);

        var link = ShareLinkBuilder.Build(Configured, claimed, Path);

        Assert.Equal(new Uri("https://media.example.org/ShareLinks/Open/abc"), link);
        Assert.DoesNotContain(ForgedHost, link.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AForgedHostCarryingAPortAndAPathDoesNotReachTheLinkEither()
    {
        // The three request-supplied pieces at once, because a forged Host is not
        // the only text a proxy in front of the server may have been talked into
        // copying, and the whole of what the request claims is what the configured
        // value replaces.
        var claimed = ShareLinkBuilder.FromRequestParts("http", ForgedHost + ":8096", "/somewhere-else");

        var link = ShareLinkBuilder.Build(Configured, claimed, Path);

        Assert.Equal(new Uri("https://media.example.org/ShareLinks/Open/abc"), link);
        Assert.DoesNotContain(ForgedHost, link.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("somewhere-else", link.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrailingSlashOnTheConfiguredValueChangesNothing()
    {
        var link = ShareLinkBuilder.Build("https://media.example.org/", null, Path);

        Assert.Equal(new Uri("https://media.example.org/ShareLinks/Open/abc"), link);
    }

    [Fact]
    public void TheProxySubPathSurvivesIntoTheLink()
    {
        // The reverse proxy case the issue asks to keep working: a server mounted
        // under a path has that path in every link it hands out, and dropping it
        // gives the guest a link that resolves to nothing.
        var link = ShareLinkBuilder.Build("https://example.org/jellyfin", null, Path);

        Assert.Equal(new Uri("https://example.org/jellyfin/ShareLinks/Open/abc"), link);
    }

    [Fact]
    public void WithNothingConfiguredTheRequestIsWhatIsLeft()
    {
        // The fallback, stated as a test rather than as a sentence: with an empty
        // setting the link is built from what the request claimed, forged or not.
        // That is the behaviour the configuration page warns about.
        var claimed = ShareLinkBuilder.FromRequestParts("https", ForgedHost, string.Empty);

        var link = ShareLinkBuilder.Build(string.Empty, claimed, Path);

        Assert.Equal(new Uri("https://attacker.example.net/ShareLinks/Open/abc"), link);
    }

    [Theory]
    [InlineData("media.example.org")]
    [InlineData("ftp://media.example.org")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://guest:secret@media.example.org")]
    [InlineData("https://media.example.org/?next=elsewhere")]
    [InlineData("https://media.example.org/#fragment")]
    [InlineData("not a url at all")]
    public void AConfiguredValueThatIsNotUsableIsRefusedRatherThanFallenBackFrom(string configured)
    {
        var claimed = ShareLinkBuilder.FromRequestParts("https", ForgedHost, string.Empty);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => ShareLinkBuilder.Build(configured, claimed, Path));

        // The refusal names the setting, because the operator reading it has to know
        // which of them to fix.
        Assert.Contains("PublicBaseUrl", refusal.Message, StringComparison.Ordinal);

        // And it is a refusal rather than a fallback. A typo in this setting must not
        // quietly restore the behaviour the setting exists to remove.
        Assert.DoesNotContain(ForgedHost, refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingConfiguredAndNothingUsableInTheRequestIsRefused()
    {
        var refusal = Assert.Throws<InvalidOperationException>(
            () => ShareLinkBuilder.Build(string.Empty, null, Path));

        Assert.Contains("PublicBaseUrl", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "media.example.org", "")]
    [InlineData("https", null, "")]
    [InlineData("https", "", "")]
    [InlineData("", "media.example.org", "")]
    public void RequestPartsThatDoNotComposeABaseUrlComposeNothing(string? scheme, string? host, string? pathBase)
    {
        Assert.Null(ShareLinkBuilder.FromRequestParts(scheme, host, pathBase));
    }

    [Fact]
    public void ARequestPathBaseWithoutItsLeadingSlashStillComposes()
    {
        Assert.Equal(
            "https://media.example.org/jellyfin",
            ShareLinkBuilder.FromRequestParts("https", "media.example.org", "jellyfin"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ShareLinks/Open/abc")]
    public void ALinkPathThatIsNotAPathIsRefused(string relativePath)
    {
        Assert.Throws<ArgumentException>(
            () => ShareLinkBuilder.Build(Configured, null, relativePath));
    }
}
