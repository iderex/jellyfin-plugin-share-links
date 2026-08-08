using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// Finding a share by the token presented for it, and the thing a file of records
/// is worth without the key (#43).
/// </summary>
/// <remarks>
/// <para>
/// The last clause of #43 asks for a test that a store file on disk cannot be used
/// to reconstruct a link. What that reduces to, once the token is not written
/// down, is that nothing readable in the file resolves: every piece of text in it
/// is presented back to the lookup and has to be refused. That is stronger than
/// searching the file for the token, which only catches the token spelled the way
/// the search expected.
/// </para>
/// <para>
/// The file here is written by <see cref="JsonSerializer"/> into a temporary
/// directory. Which serialiser the plugin's store uses, and what its file looks
/// like, is #35 and #37; this needs a file of records and takes no position on
/// either. Nothing starts a server, reaches the network or needs a privilege.
/// </para>
/// </remarks>
public class ShareLookupTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("share links lookup test key, long enough");

    private static ShareRecord RecordFor(string storedValue, string id) => new()
    {
        SchemaVersion = ShareRecord.CurrentSchemaVersion,
        Id = new Guid(id),
        ItemId = new Guid("11111111-1111-1111-1111-111111111111"),
        InvitedUserIds = [new Guid("22222222-2222-2222-2222-222222222222")],
        CreatedByUserId = new Guid("44444444-4444-4444-4444-444444444444"),
        CreatedAt = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        ExpiresAt = new DateTimeOffset(2026, 1, 9, 3, 4, 5, TimeSpan.Zero),
        TokenHash = storedValue,
    };

    [Fact]
    public void TheLookupFindsTheRecordTheTokenBelongsTo()
    {
        var wanted = ShareTokens.Mint();

        var records = new List<ShareRecord>
        {
            RecordFor(ShareTokenHash.Compute(Key, ShareTokens.Mint()), "55555555-5555-5555-5555-555555555551"),
            RecordFor(ShareTokenHash.Compute(Key, wanted), "55555555-5555-5555-5555-555555555552"),
            RecordFor(ShareTokenHash.Compute(Key, ShareTokens.Mint()), "55555555-5555-5555-5555-555555555553"),
        };

        var found = ShareLookup.ByToken(records, Key, wanted);

        Assert.NotNull(found);
        Assert.Equal(new Guid("55555555-5555-5555-5555-555555555552"), found!.Id);
    }

    [Fact]
    public void ATokenNoRecordBelongsToFindsNothing()
    {
        var records = new List<ShareRecord>
        {
            RecordFor(ShareTokenHash.Compute(Key, ShareTokens.Mint()), "55555555-5555-5555-5555-555555555551"),
        };

        Assert.Null(ShareLookup.ByToken(records, Key, ShareTokens.Mint()));
    }

    [Fact]
    public void TheRightTokenUnderAnotherInstallsKeyFindsNothing()
    {
        var wanted = ShareTokens.Mint();
        var records = new List<ShareRecord> { RecordFor(ShareTokenHash.Compute(Key, wanted), "55555555-5555-5555-5555-555555555551") };
        var somebodyElsesKey = Encoding.UTF8.GetBytes("a different install's key, long enough");

        Assert.Null(ShareLookup.ByToken(records, somebodyElsesKey, wanted));
    }

    [Fact]
    public void AnEmptyStoreFindsNothing()
    {
        Assert.Null(ShareLookup.ByToken([], Key, ShareTokens.Mint()));
    }

    [Fact]
    public void ARecordHoldingTheTokenItselfDoesNotAnswerForIt()
    {
        // The store written by the mistake this issue exists against. If a record
        // carrying the raw token still resolved, the repair would leave every
        // record written before it working, and there would be nothing to notice.
        var token = ShareTokens.Mint();

        Assert.Null(ShareLookup.ByToken([RecordFor(token, "55555555-5555-5555-5555-555555555551")], Key, token));
    }

    [Fact]
    public void AStoreFileOnDiskCannotBeUsedToReconstructALink()
    {
        var tokens = new[] { ShareTokens.Mint(), ShareTokens.Mint(), ShareTokens.Mint() };
        var records = new List<ShareRecord>
        {
            RecordFor(ShareTokenHash.Compute(Key, tokens[0]), "55555555-5555-5555-5555-555555555551"),
            RecordFor(ShareTokenHash.Compute(Key, tokens[1]), "55555555-5555-5555-5555-555555555552"),
            RecordFor(ShareTokenHash.Compute(Key, tokens[2]), "55555555-5555-5555-5555-555555555553"),
        };

        var directory = Directory.CreateTempSubdirectory("share-links-lookup-");
        try
        {
            var file = Path.Combine(directory.FullName, "shares.json");
            File.WriteAllText(file, JsonSerializer.Serialize(records));

            var written = File.ReadAllText(file);

            // The file holds the records, so a test that found nothing in it
            // would prove nothing about what was searched.
            Assert.Contains("55555555-5555-5555-5555-555555555552", written, StringComparison.Ordinal);

            foreach (var token in tokens)
            {
                Assert.DoesNotContain(token, written, StringComparison.Ordinal);
            }

            // Everything the file says, offered back as a token. This is the
            // clause of #43 about reconstruction: a reader who has the file and
            // not the key holds nothing that resolves, including the stored
            // values themselves.
            var offered = 0;
            foreach (var candidate in EveryStringIn(written))
            {
                offered++;
                Assert.Null(ShareLookup.ByToken(records, Key, candidate));
            }

            Assert.True(offered >= records.Count, $"only {offered} pieces of text were taken out of the file, which is fewer than the {records.Count} stored values it holds, so the walk over the document missed some of it");

            // The same file with the key in hand does resolve, which is what says
            // the refusals above are the file's doing rather than a lookup that
            // answers no to everything.
            Assert.NotNull(ShareLookup.ByToken(records, Key, tokens[1]));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static IEnumerable<string> EveryStringIn(string document)
    {
        using var parsed = JsonDocument.Parse(document);
        foreach (var value in Strings(parsed.RootElement))
        {
            yield return value;
        }
    }

    private static IEnumerable<string> Strings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in Strings(item))
                    {
                        yield return value;
                    }
                }

                break;

            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    foreach (var value in Strings(member.Value))
                    {
                        yield return value;
                    }
                }

                break;

            default:
                break;
        }
    }
}
