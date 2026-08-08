using System;
using System.Globalization;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// Builds the absolute link an operator hands to a guest (#49).
/// </summary>
/// <remarks>
/// <para>
/// The obvious source for the host in that link is the request the operator's
/// dashboard made, and that source is attacker-influenced. A Host header, or a
/// forwarded header a proxy was talked into copying, is text a caller supplies;
/// a link built from it points wherever the caller said, and the operator then
/// sends that link to a guest who has no way to tell it apart from the real one.
/// The guest signs in at the wrong place, or fetches nothing and asks the
/// operator to re-send, which is the same attack with a slower first step.
/// </para>
/// <para>
/// So the configured value wins whenever there is one, and the request is read
/// only when the configuration is empty. The fallback exists because a plugin
/// that produces no link at all until a setting is filled in is a plugin an
/// operator meets as broken, and because on a server reached by one name and
/// nothing else the request does carry the right answer. It is a fallback and
/// not a default: <c>PublicBaseUrl</c> is what a deployment behind a reverse
/// proxy has to set, and the configuration page says so.
/// </para>
/// <para>
/// A configured value that cannot be used is refused rather than fallen back
/// from. Falling back would mean a typo in the setting quietly restores exactly
/// the behaviour the setting exists to remove, and it would do it on the path
/// nobody re-reads once it has worked once.
/// </para>
/// <para>
/// What this routine does not decide is where the link points. The guest route
/// and its path are #68's, so the caller passes the path in, and nothing here
/// names a route that does not exist yet.
/// </para>
/// <para>
/// No ASP.NET type is referenced here, because the plugin project does not
/// reference the framework at all:
/// </para>
/// <code>
/// grep -c 'Microsoft.AspNetCore' Jellyfin.Plugin.ShareLinks/Jellyfin.Plugin.ShareLinks.csproj
/// 0
/// </code>
/// <para>
/// The request arrives as the three pieces of text a request carries, which is
/// what a Host header is, and <see cref="FromRequestParts"/> is the one place
/// that text becomes a URL. When the guest route lands it hands over
/// <c>Request.Scheme</c>, <c>Request.Host</c> and <c>Request.PathBase</c>, and
/// the trust boundary stays in this file rather than spreading into a
/// controller.
/// </para>
/// </remarks>
public static class ShareLinkBuilder
{
    /// <summary>
    /// Composes what the request claims the server is reachable at.
    /// </summary>
    /// <param name="scheme">The request scheme, <c>http</c> or <c>https</c>.</param>
    /// <param name="host">The host and optional port the request carried. This is caller-supplied text.</param>
    /// <param name="pathBase">The path the server is mounted under, empty when it is mounted at the root.</param>
    /// <returns>The base URL the request claims, or <see langword="null"/> when the parts do not compose one.</returns>
    public static string? FromRequestParts(string? scheme, string? host, string? pathBase)
    {
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var trimmedPathBase = (pathBase ?? string.Empty).Trim();
        if (trimmedPathBase.Length > 0 && trimmedPathBase[0] != '/')
        {
            trimmedPathBase = "/" + trimmedPathBase;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}://{1}{2}",
            scheme.Trim(),
            host.Trim(),
            trimmedPathBase.TrimEnd('/'));
    }

    /// <summary>
    /// Builds the absolute link for a share.
    /// </summary>
    /// <param name="configuredBaseUrl">The value of the <c>PublicBaseUrl</c> setting, empty when the operator has set none.</param>
    /// <param name="requestBaseUrl">What the request claimed, from <see cref="FromRequestParts"/>. Read only when nothing is configured.</param>
    /// <param name="relativePath">The path the link points at, beginning with a slash. The route it names is the caller's to decide.</param>
    /// <returns>The absolute link.</returns>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is not a path beginning with a slash.</exception>
    /// <exception cref="InvalidOperationException">The configured value is not a usable base URL, or nothing is configured and the request does not carry one either.</exception>
    public static Uri Build(string? configuredBaseUrl, string? requestBaseUrl, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        if (relativePath.Length == 0 || relativePath[0] != '/')
        {
            throw new ArgumentException("A share link path begins with a slash.", nameof(relativePath));
        }

        var configured = (configuredBaseUrl ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            if (!TryReadBaseUrl(configured, out var fromConfiguration))
            {
                throw new InvalidOperationException(
                    "PublicBaseUrl is set to a value that is not an absolute http or https URL, so no link can be built from it. The request is not used instead, because that is the source this setting exists to stop trusting.");
            }

            return Compose(fromConfiguration, relativePath);
        }

        if (!TryReadBaseUrl((requestBaseUrl ?? string.Empty).Trim(), out var fromRequest))
        {
            throw new InvalidOperationException(
                "PublicBaseUrl is empty and the request does not carry an absolute http or https base URL either, so there is nothing to build a link from. Set PublicBaseUrl.");
        }

        return Compose(fromRequest, relativePath);
    }

    // What a base URL has to be to carry a link somebody else will open. Absolute,
    // because a relative one names no server. http or https, because those are the
    // schemes a browser will follow and anything else is a value that arrived from
    // somewhere it should not have. No user information, because credentials in a
    // link an operator forwards are credentials in whatever forwarded it. No query
    // and no fragment, because both would be dropped or reordered by the
    // composition below and a value that is silently discarded is worse than one
    // that is refused.
    private static bool TryReadBaseUrl(string value, out Uri baseUrl)
    {
        baseUrl = null!;

        if (value.Length == 0 || !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            && !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
        {
            return false;
        }

        if (parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0)
        {
            return false;
        }

        baseUrl = parsed;
        return true;
    }

    // The path of the base URL is kept rather than replaced, which is the reverse
    // proxy case: a server mounted at /jellyfin has that segment in every link it
    // hands out, and Uri's own relative resolution would drop it for a path that
    // begins with a slash.
    private static Uri Compose(Uri baseUrl, string relativePath)
    {
        var origin = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

        return new Uri(
            string.Format(CultureInfo.InvariantCulture, "{0}{1}", origin, relativePath),
            UriKind.Absolute);
    }
}
