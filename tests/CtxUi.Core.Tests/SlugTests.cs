using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("echo")]
    [InlineData("lab-5")]
    [InlineData("a")]
    [InlineData("0x")]
    public void Valid_slug_is_returned(string value) => Assert.Equal(value, Slug.Parse(value));

    [Fact]
    public void Surrounding_whitespace_is_trimmed() => Assert.Equal("echo", Slug.Parse("  echo "));

    [Fact]
    public void Sixty_four_characters_are_allowed() => Assert.Equal(64, Slug.Parse(new string('a', 64)).Length);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Echo")]
    [InlineData("-echo")]
    [InlineData("echo-")]
    [InlineData("my_slug")]
    [InlineData("отчёты")]
    [InlineData("a/b")]
    [InlineData("ec\nho")]
    public void Invalid_slug_is_rejected(string? value) =>
        Assert.Throws<DomainException>(() => Slug.Parse(value));

    [Fact]
    public void Sixty_five_characters_are_rejected() =>
        Assert.Throws<DomainException>(() => Slug.Parse(new string('a', 65)));
}
