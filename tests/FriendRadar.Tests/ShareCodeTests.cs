using FriendRadar.Protocol;

namespace FriendRadar.Tests;

public class ShareCodeTests
{
    [Fact]
    public void GeneratedCodesArePrefixedAndGrouped()
    {
        var code = ShareCode.Generate();

        Assert.StartsWith("FR-", code, StringComparison.Ordinal);
        Assert.Equal("FR-XXXX-XXXX-XXXX-XXXX".Length, code.Length);
    }

    [Fact]
    public void GeneratedCodesRoundTripThroughNormalisation()
    {
        var code = ShareCode.Generate();

        Assert.NotNull(ShareCode.Normalize(code));
        Assert.True(ShareCode.Matches(code, code));
    }

    [Fact]
    public void GeneratedCodesAreDistinct()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => ShareCode.Generate()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, codes.Count);
    }

    [Theory]
    [InlineData("fr-9gq4-k72m-xpz1-d8v0")]
    [InlineData("FR9GQ4K72MXPZ1D8V0")]
    [InlineData("  FR 9GQ4 K72M XPZ1 D8V0  ")]
    public void FormattingAndCaseAreIgnored(string variant)
    {
        Assert.True(ShareCode.Matches("FR-9GQ4-K72M-XPZ1-D8V0", variant));
    }

    [Theory]
    [InlineData("FR-OGQ4-K72M-XPZI-D8VO", "FR-0GQ4-K72M-XPZ1-D8V0")]
    [InlineData("FR-1GQ4-K72M-XPZL-D8V0", "FR-1GQ4-K72M-XPZ1-D8V0")]
    public void LookAlikeCharactersAreFolded(string typed, string actual)
    {
        Assert.True(ShareCode.Matches(typed, actual));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FR-9GQ4")]
    [InlineData("FR-9GQ4-K72M-XPZ1-D8V0-EXTRA")]
    [InlineData("FR-9GQ4-K72M-XPZ1-D8V!")]
    public void NonsenseIsRejected(string? input)
    {
        Assert.Null(ShareCode.Normalize(input));
        Assert.False(ShareCode.Matches(input, ShareCode.Generate()));
    }

    [Fact]
    public void DifferentCodesDoNotMatch()
    {
        Assert.False(ShareCode.Matches(ShareCode.Generate(), ShareCode.Generate()));
    }
}
