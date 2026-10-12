using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class ClientMetadataTests
{
    private static KeyValuePair<string, string>[] Pairs(params (string Key, string Value)[] items) =>
        items.Select(item => KeyValuePair.Create(item.Key, item.Value)).ToArray();

    [Fact]
    public void Valid_pairs_are_copied()
    {
        var metadata = ClientMetadata.Parse(Pairs(("storage-mb", "50"), ("group", "4419")));

        Assert.Equal(2, metadata.Count);
        Assert.Equal("50", metadata["storage-mb"]);
        Assert.Equal("4419", metadata["GROUP"]);
    }

    [Fact]
    public void Missing_pairs_give_empty_metadata() => Assert.Empty(ClientMetadata.Parse(null));

    [Theory]
    [InlineData("")]
    [InlineData("storage mb")]
    [InlineData("ключ")]
    [InlineData("-a")]
    [InlineData("a-")]
    [InlineData("a:b")]
    public void Invalid_key_is_rejected(string key) =>
        Assert.Throws<DomainException>(() => ClientMetadata.Parse(Pairs((key, "1"))));

    [Fact]
    public void Value_with_line_break_is_rejected() =>
        Assert.Throws<DomainException>(() => ClientMetadata.Parse(Pairs(("a", "1\r\nX-Evil: 1"))));

    [Fact]
    public void Too_long_value_is_rejected() =>
        Assert.Throws<DomainException>(() => ClientMetadata.Parse(Pairs(("a", new string('x', ClientMetadata.MaxValueLength + 1)))));

    [Fact]
    public void Same_key_in_different_case_is_rejected() =>
        Assert.Throws<DomainException>(() => ClientMetadata.Parse(Pairs(("Group", "1"), ("group", "2"))));

    [Fact]
    public void Too_many_pairs_are_rejected()
    {
        var pairs = Enumerable.Range(0, ClientMetadata.MaxEntries + 1).Select(i => KeyValuePair.Create($"k{i}", "v"));

        Assert.Throws<DomainException>(() => ClientMetadata.Parse(pairs));
    }
}
