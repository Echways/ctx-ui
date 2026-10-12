using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class ClientNameTests
{
    [Fact]
    public void Name_is_trimmed() => Assert.Equal("Иванов 4419", ClientName.Parse("  Иванов 4419 "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Иванов\n4419")]
    public void Empty_or_multiline_name_is_rejected(string? value) =>
        Assert.Throws<DomainException>(() => ClientName.Parse(value));

    [Fact]
    public void Name_longer_than_limit_is_rejected() =>
        Assert.Throws<DomainException>(() => ClientName.Parse(new string('я', ClientName.MaxLength + 1)));

    [Theory]
    [InlineData("Иванов 4419", "иванов 4419")]
    [InlineData("Ivanov", "IVANOV")]
    [InlineData("Ёлкин", "ёлкин")]
    public void Key_ignores_case_including_cyrillic(string first, string second) =>
        Assert.Equal(ClientName.Key(first), ClientName.Key(second));

    [Fact]
    public void Key_differs_for_different_names() =>
        Assert.NotEqual(ClientName.Key("Иванов"), ClientName.Key("Петров"));
}
