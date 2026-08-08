using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// Finds the share a presented token belongs to (#43).
/// </summary>
/// <remarks>
/// <para>
/// This is the lookup the issue asks to be by hash rather than by token. It holds
/// no comparison of its own: every decision about whether a record answers for a
/// token is made by <see cref="ShareTokenHash.Matches"/>, which is the one place
/// the fixed-time comparison lives.
/// </para>
/// <para>
/// The scan does not stop at the first record that answers. Stopping early makes
/// the time a lookup takes a measurement of where in the store the record sits,
/// and the order of a store is something an operator's own actions decide, so a
/// caller could learn how many shares were created before theirs. At most one
/// record can answer, because the value is 256 bits wide, so continuing costs a
/// comparison per record and buys the same answer.
/// </para>
/// <para>
/// What this does not do is decide whether the share it found may be resolved.
/// Expiry, revocation, whether the caller is invited and what the share reaches
/// are #45, #46, #44 and #47, and the routine that puts them in one place is #48.
/// Finding the record and admitting the caller are two different questions and
/// this answers the first.
/// </para>
/// </remarks>
public static class ShareLookup
{
    /// <summary>
    /// Finds the record a presented token belongs to.
    /// </summary>
    /// <param name="records">The records to search.</param>
    /// <param name="key">The install's key, as <see cref="ShareTokenHash"/> takes it.</param>
    /// <param name="presentedToken">The token the caller presented.</param>
    /// <returns>The record the token belongs to, or <c>null</c> when no record does.</returns>
    public static ShareRecord? ByToken(IReadOnlyList<ShareRecord> records, ReadOnlySpan<byte> key, string presentedToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrEmpty(presentedToken);

        ShareRecord? found = null;

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (ShareTokenHash.Matches(key, presentedToken, record.TokenHash))
            {
                found = record;
            }
        }

        return found;
    }
}
