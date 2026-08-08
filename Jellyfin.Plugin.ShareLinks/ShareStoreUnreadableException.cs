using System;
using System.Globalization;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// Thrown when the share store exists and cannot be read (#35).
/// </summary>
/// <remarks>
/// The alternative is returning an empty list, and that is the failure rather
/// than the handling of it. A store whose bytes are damaged and a server that has
/// never had a share on it would then look the same to every caller, and the
/// first thing the plugin would do with that answer is offer to write a fresh
/// empty store over the damaged one. An operator can restore a file; nobody can
/// restore a file that was quietly replaced.
/// </remarks>
public sealed class ShareStoreUnreadableException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStoreUnreadableException"/> class.
    /// </summary>
    /// <param name="path">The store that could not be read.</param>
    /// <param name="reason">What was wrong with it.</param>
    public ShareStoreUnreadableException(string path, string reason)
        : base(Describe(path, reason))
    {
        Path = path;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStoreUnreadableException"/> class.
    /// </summary>
    /// <param name="path">The store that could not be read.</param>
    /// <param name="reason">What was wrong with it.</param>
    /// <param name="innerException">The failure underneath this one.</param>
    public ShareStoreUnreadableException(string path, string reason, Exception innerException)
        : base(Describe(path, reason), innerException)
    {
        Path = path;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStoreUnreadableException"/> class.
    /// </summary>
    public ShareStoreUnreadableException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStoreUnreadableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public ShareStoreUnreadableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStoreUnreadableException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The failure underneath this one.</param>
    public ShareStoreUnreadableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the store that could not be read, or null when this instance was built without one.
    /// </summary>
    public string? Path { get; }

    private static string Describe(string path, string reason)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"The share store at {path} exists and could not be read: {reason}. It is not being treated as an empty store, because that would hide the loss and invite the next write to overwrite whatever is left.");
}
