using CtxUi.Core.Admins;

namespace CtxUi.Core.Tests;

public class AdminNameTests
{
    [Theory]
    [InlineData("root")]
    [InlineData("a")]
    [InlineData("Ivan.Petrov")]
    [InlineData("admin_2-b")]
    public void Latin_letters_digits_dot_dash_and_underscore_are_accepted(string name) =>
        Assert.Equal(name, AdminName.Parse(name));

    [Fact]
    public void Surrounding_spaces_are_dropped() => Assert.Equal("root", AdminName.Parse("  root\n"));

    [Fact]
    public void Longest_name_is_64_characters() =>
        Assert.Equal(new string('a', 64), AdminName.Parse(new string('a', 64)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("два слова")]
    [InlineData("иван")]
    [InlineData("root admin")]
    [InlineData("root@example.org")]
    [InlineData("root\u0000")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Anything_else_is_refused_in_words(string? name)
    {
        var exception = Assert.Throws<DomainException>(() => AdminName.Parse(name));

        Assert.Contains("логин", exception.Message);
    }

    [Fact]
    public void Key_ignores_case() => Assert.Equal(AdminName.Key("Root"), AdminName.Key("rOOT"));
}
