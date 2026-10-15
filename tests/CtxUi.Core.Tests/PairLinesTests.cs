namespace CtxUi.Core.Tests;

public class PairLinesTests
{
    [Fact]
    public void Each_line_is_split_at_the_first_equals_sign()
    {
        var pairs = PairLines.Parse(["group=4419", "Authorization=Bearer a=b", "empty="]);

        Assert.Equal(
            [KeyValuePair.Create("group", "4419"), KeyValuePair.Create("Authorization", "Bearer a=b"), KeyValuePair.Create("empty", "")],
            pairs);
    }

    [Fact]
    public void No_lines_give_no_pairs()
    {
        Assert.Empty(PairLines.Parse(null));
        Assert.Empty(PairLines.Parse([]));
        Assert.Empty(PairLines.ParseText(null));
        Assert.Empty(PairLines.ParseText(" \n\r\n"));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("=4419")]
    [InlineData("")]
    public void Line_without_a_key_or_a_sign_is_refused_by_name(string line)
    {
        var exception = Assert.Throws<DomainException>(() => PairLines.Parse(["ok=1", line]));

        Assert.Contains($"«{line}»", exception.Message);
        Assert.Contains("ключ=значение", exception.Message);
    }

    [Fact]
    public void Text_from_a_form_is_read_line_by_line_without_blank_ones()
    {
        var pairs = PairLines.ParseText("  group=4419\r\n\r\n\tstorage-mb=50  \n");

        Assert.Equal([KeyValuePair.Create("group", "4419"), KeyValuePair.Create("storage-mb", "50")], pairs);
    }

    [Fact]
    public void Broken_line_in_a_form_is_refused_too() =>
        Assert.Throws<DomainException>(() => PairLines.ParseText("group=4419\nпросто слово"));
}
