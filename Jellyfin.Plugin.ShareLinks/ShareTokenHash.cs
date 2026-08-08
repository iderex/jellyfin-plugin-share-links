using System;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// The keyed hash a share record holds in place of its token (#43).
/// </summary>
/// <remarks>
/// <para>
/// A store that holds tokens is a file that produces working links. What is
/// written down instead is a keyed hash, so a copy of the store is a list of
/// values that resolve nothing without the key, and the key is not in the store.
/// What the key is made of, where it lives and how it is rotated is #28; this
/// routine takes it as bytes and takes no position on any of that.
/// </para>
/// <para>
/// Keyed rather than plain. A plain hash of a 256-bit token is not reversible
/// either, but it is the same value on every server, so a table built once is a
/// table that works everywhere. The key makes the value local to one install,
/// which is what turns a leaked store into a leak of that install alone.
/// </para>
/// <para>
/// HMAC-SHA256, from the framework, over the token's UTF-8 bytes. The token is
/// already 256 bits of cryptographic material and is not a password, so a slow
/// password hash would buy nothing that the token's own entropy does not already
/// buy, and would cost a resolution the time it takes on every request.
/// </para>
/// <para>
/// The encoded form is base64url with the padding dropped, the same encoding
/// <see cref="ShareTokens"/> uses, so a person reading the store during support
/// sees one alphabet rather than two.
/// </para>
/// <para>
/// The comparison is <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// and never an ordinary one. An ordinary comparison returns on the first
/// differing byte, and how long it took is a measurement of how much of the value
/// was right, taken on a path that runs before the caller has been shown to be
/// invited. The byte variables below are named so that
/// <c>token-compared-in-constant-time</c> in the greppable invariant lint reaches
/// them: that check matches on the identifier, so a comparison over bytes called
/// anything else would walk past it.
/// </para>
/// <para>
/// Nothing calls this yet. There is no route that creates or resolves a share, so
/// what is here is the routine and its proof, not a request travelling through it.
/// </para>
/// </remarks>
public static class ShareTokenHash
{
    /// <summary>
    /// The number of bytes in one computed value.
    /// </summary>
    public const int DigestBytes = 32;

    /// <summary>
    /// The number of characters in the encoded form.
    /// </summary>
    public const int EncodedLength = 43;

    /// <summary>
    /// The shortest key this routine accepts, in bytes.
    /// </summary>
    /// <remarks>
    /// A key shorter than the digest it keys adds nothing and hides that it adds
    /// nothing, so it is refused rather than accepted with a warning nobody reads.
    /// </remarks>
    public const int MinimumKeyBytes = 32;

    /// <summary>
    /// Computes the value a record holds for a token.
    /// </summary>
    /// <param name="key">The install's key. At least <see cref="MinimumKeyBytes"/> bytes.</param>
    /// <param name="token">The token as it appears in a link.</param>
    /// <returns>The encoded value, <see cref="EncodedLength"/> characters long.</returns>
    public static string Compute(ReadOnlySpan<byte> key, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        RefuseAShortKey(key);

        Span<byte> computedHash = stackalloc byte[DigestBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(token), computedHash);
        return Base64Url.EncodeToString(computedHash);
    }

    /// <summary>
    /// Decides whether a presented token is the one a stored value was computed
    /// from.
    /// </summary>
    /// <param name="key">The install's key. At least <see cref="MinimumKeyBytes"/> bytes.</param>
    /// <param name="presentedToken">The token the caller presented.</param>
    /// <param name="storedValue">The value read out of a record.</param>
    /// <returns><c>true</c> when the token produces the stored value under this key.</returns>
    /// <remarks>
    /// A stored value that is not the shape this routine writes is refused rather
    /// than throwing. It reaches here from a file rather than from a caller, and a
    /// resolution that dies on one damaged record is a resolution that stops
    /// answering for every other share in the store.
    /// </remarks>
    public static bool Matches(ReadOnlySpan<byte> key, string presentedToken, string storedValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(presentedToken);
        ArgumentNullException.ThrowIfNull(storedValue);
        RefuseAShortKey(key);

        // IsValid before the try. The try returns false for a destination that is
        // too small and for a length that cannot decode, but it THROWS on a
        // character outside the alphabet, which a file edited by hand or half
        // written by a crash can easily hold. Without this line one damaged record
        // is an exception out of the resolution path rather than a record that
        // does not answer.
        Span<byte> storedHash = stackalloc byte[DigestBytes];
        if (!Base64Url.IsValid(storedValue)
            || !Base64Url.TryDecodeFromChars(storedValue, storedHash, out var decoded)
            || decoded != DigestBytes)
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[DigestBytes];
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(presentedToken), presentedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, storedHash);
    }

    private static void RefuseAShortKey(ReadOnlySpan<byte> key)
    {
        if (key.Length < MinimumKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(key),
                key.Length,
                $"The key is shorter than the {MinimumKeyBytes} bytes this routine requires.");
        }
    }
}
