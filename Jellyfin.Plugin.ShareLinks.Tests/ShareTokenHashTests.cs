using System;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// The value a record holds in place of a token (#43).
/// </summary>
/// <remarks>
/// <para>
/// What these can judge is that the value is derived from the token and from the
/// key, that a token produces it again, and that nothing else does. What no test
/// here judges is that the comparison takes the same time whichever byte differs:
/// a timing assertion on a shared runner measures the runner. That property is
/// held by <c>token-compared-in-constant-time</c> in the greppable invariant lint,
/// which refuses an ordinary comparison over a name carrying token, secret or
/// hash, and by the byte variables in <see cref="ShareTokenHash"/> being named so
/// that check reaches them.
/// </para>
/// <para>
/// The keys are literal text rather than drawn bytes. A key drawn in a test would
/// be a second file calling the cryptographic generator, which
/// <c>token-bytes-come-from-one-routine</c> refuses, and a fixed key is what makes
/// a failing run reproduce.
/// </para>
/// </remarks>
public class ShareTokenHashTests
{
    private static readonly byte[] KeyOne = Encoding.UTF8.GetBytes("share links test key one, long enough");
    private static readonly byte[] KeyTwo = Encoding.UTF8.GetBytes("share links test key two, long enough");

    [Fact]
    public void TheKeysThisSuiteUsesAreLongEnoughToBeAccepted()
    {
        // Otherwise every test below passes by throwing the same exception, which
        // is a suite that reports on the argument check and on nothing else.
        Assert.True(KeyOne.Length >= ShareTokenHash.MinimumKeyBytes);
        Assert.True(KeyTwo.Length >= ShareTokenHash.MinimumKeyBytes);
    }

    [Fact]
    public void TheValueIsNotTheTokenAndDoesNotCarryIt()
    {
        var token = ShareTokens.Mint();

        var value = ShareTokenHash.Compute(KeyOne, token);

        Assert.NotEqual(token, value);
        Assert.DoesNotContain(token, value, StringComparison.Ordinal);

        // A prefix would be enough to shorten a search, so the absence asserted is
        // not only of the whole token.
        Assert.DoesNotContain(token.AsSpan(0, 8).ToString(), value, StringComparison.Ordinal);
    }

    [Fact]
    public void OneTokenUnderOneKeyAlwaysProducesTheSameValue()
    {
        var token = ShareTokens.Mint();

        Assert.Equal(ShareTokenHash.Compute(KeyOne, token), ShareTokenHash.Compute(KeyOne, token));
    }

    [Fact]
    public void OneTokenUnderTwoKeysProducesTwoValues()
    {
        // This is the whole of what keying buys. Without it the value is the same
        // on every install, so a table built once against a plain hash of the
        // alphabet resolves a store copied from anywhere.
        var token = ShareTokens.Mint();

        Assert.NotEqual(ShareTokenHash.Compute(KeyOne, token), ShareTokenHash.Compute(KeyTwo, token));
    }

    [Fact]
    public void TwoTokensUnderOneKeyProduceTwoValues()
    {
        Assert.NotEqual(ShareTokenHash.Compute(KeyOne, ShareTokens.Mint()), ShareTokenHash.Compute(KeyOne, ShareTokens.Mint()));
    }

    [Fact]
    public void TheEncodedFormIsTheLengthAndAlphabetTheRoutineDeclares()
    {
        var value = ShareTokenHash.Compute(KeyOne, ShareTokens.Mint());

        Assert.Equal(ShareTokenHash.EncodedLength, value.Length);

        foreach (var character in value)
        {
            Assert.True(ShareTokens.Alphabet.Contains(character), $"the encoded form carries '{character}', which is outside the alphabet a link can hold unescaped");
        }
    }

    [Fact]
    public void ThePresentedTokenTheValueWasComputedFromIsAccepted()
    {
        var token = ShareTokens.Mint();

        Assert.True(ShareTokenHash.Matches(KeyOne, token, ShareTokenHash.Compute(KeyOne, token)));
    }

    [Fact]
    public void AnyOtherPresentedTokenIsRefused()
    {
        var token = ShareTokens.Mint();
        var stored = ShareTokenHash.Compute(KeyOne, token);

        Assert.False(ShareTokenHash.Matches(KeyOne, ShareTokens.Mint(), stored));

        // The near miss. One character different is the case an early-returning
        // comparison would answer fastest.
        var almost = token[..^1] + (token[^1] is 'A' ? "B" : "A");
        Assert.NotEqual(token, almost);
        Assert.False(ShareTokenHash.Matches(KeyOne, almost, stored));
    }

    [Fact]
    public void TheRightTokenUnderTheWrongKeyIsRefused()
    {
        var token = ShareTokens.Mint();

        Assert.False(ShareTokenHash.Matches(KeyTwo, token, ShareTokenHash.Compute(KeyOne, token)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64url at all !!")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AStoredValueOfTheWrongShapeIsRefusedRatherThanThrown(string stored)
    {
        // These arrive from a file rather than from a caller. A resolution that
        // throws on one damaged record stops answering for every other share in
        // the same store, which turns a corrupt line into an outage.
        Assert.False(ShareTokenHash.Matches(KeyOne, ShareTokens.Mint(), stored));
    }

    [Fact]
    public void AStoredValueThatIsTheTokenItselfIsRefused()
    {
        // The shape a store written by an earlier mistake would have. It has to
        // answer no, or the repair that stops writing tokens down leaves every
        // record written before it still working.
        var token = ShareTokens.Mint();

        Assert.False(ShareTokenHash.Matches(KeyOne, token, token));
    }

    [Fact]
    public void AKeyShorterThanTheFloorIsRefused()
    {
        var tooShort = new byte[ShareTokenHash.MinimumKeyBytes - 1];
        var token = ShareTokens.Mint();
        var stored = ShareTokenHash.Compute(KeyOne, token);

        Assert.Throws<ArgumentOutOfRangeException>(() => ShareTokenHash.Compute(tooShort, token));
        Assert.Throws<ArgumentOutOfRangeException>(() => ShareTokenHash.Matches(tooShort, token, stored));
    }

    [Fact]
    public void AnEmptyTokenIsRefused()
    {
        Assert.Throws<ArgumentException>(() => ShareTokenHash.Compute(KeyOne, string.Empty));
        Assert.Throws<ArgumentException>(() => ShareTokenHash.Matches(KeyOne, string.Empty, "AAAA"));
    }
}
