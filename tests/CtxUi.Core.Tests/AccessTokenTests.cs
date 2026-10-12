using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class AccessTokenTests
{
    private const string Sample = "tnk_0123456789abcdefghijABCDEFGHIJ01";

    [Fact]
    public void Generated_token_is_well_formed_and_unique()
    {
        var first = AccessToken.Generate();
        var second = AccessToken.Generate();

        Assert.True(AccessToken.IsWellFormed(first));
        Assert.StartsWith("tnk_", first);
        Assert.Equal(36, first.Length);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tnk_")]
    [InlineData("tnk_short")]
    [InlineData("abc_0123456789abcdefghijABCDEFGHIJ01")]
    [InlineData("tnk_0123456789abcdefghijABCDEFGHIJ0/")]
    [InlineData("tnk_0123456789abcdefghijABCDEFGHIJ0я")]
    [InlineData("tnk_0123456789abcdefghijABCDEFGHIJ012")]
    public void Malformed_value_is_rejected(string? value) =>
        Assert.False(AccessToken.IsWellFormed(value));

    [Fact]
    public void Sample_token_is_well_formed() => Assert.True(AccessToken.IsWellFormed(Sample));

    [Fact]
    public void Hash_is_lowercase_sha256_hex() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AccessToken.Hash("abc"));

    [Fact]
    public void Hash_differs_for_different_tokens() =>
        Assert.NotEqual(AccessToken.Hash(Sample), AccessToken.Hash("tnk_0123456789abcdefghijABCDEFGHIJ02"));

    [Fact]
    public void Display_prefix_shows_first_six_secret_characters() =>
        Assert.Equal("tnk_012345", AccessToken.DisplayPrefix(Sample));
}
