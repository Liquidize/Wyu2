using Wyu2.Protocol;

namespace Wyu2.Tests;

public class ShareCodeTests
{
    [Fact]
    public void GeneratedCodesArePrefixedAndGrouped()
    {
        var code = ShareCode.Generate();

        Assert.StartsWith("WY-", code, StringComparison.Ordinal);
        Assert.Equal("WY-XXXX-XXXX-XXXX-XXXX".Length, code.Length);
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
    [InlineData("wy-9gq4-k72m-xpz1-d8v0")]
    [InlineData("WY9GQ4K72MXPZ1D8V0")]
    [InlineData("  WY 9GQ4 K72M XPZ1 D8V0  ")]
    public void FormattingAndCaseAreIgnored(string variant)
    {
        Assert.True(ShareCode.Matches("WY-9GQ4-K72M-XPZ1-D8V0", variant));
    }

    [Theory]
    [InlineData("WY-OGQ4-K72M-XPZI-D8VO", "WY-0GQ4-K72M-XPZ1-D8V0")]
    [InlineData("WY-1GQ4-K72M-XPZL-D8V0", "WY-1GQ4-K72M-XPZ1-D8V0")]
    public void LookAlikeCharactersAreFolded(string typed, string actual)
    {
        Assert.True(ShareCode.Matches(typed, actual));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WY-9GQ4")]
    [InlineData("WY-9GQ4-K72M-XPZ1-D8V0-EXTRA")]
    [InlineData("WY-9GQ4-K72M-XPZ1-D8V!")]
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
