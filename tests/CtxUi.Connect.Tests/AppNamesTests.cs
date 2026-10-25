using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class AppNamesTests
{
    [Theory]
    [InlineData("Claude", "Claude")]
    [InlineData("  Claude   Code ", "Claude Code")]
    [InlineData("Cur\u0000sor\r\nIDE", "Cur sor IDE")]
    [InlineData("Evil‮edoC", "Evil edoC")]
    [InlineData(null, "")]
    public void Names_are_cleaned(string? given, string expected) => Assert.Equal(expected, AppNames.Clean(given));

    [Fact]
    public void Long_names_are_cut()
    {
        var name = AppNames.Clean(new string('a', 500));

        Assert.Equal(AppNames.MaxLength + 1, name.Length);
        Assert.EndsWith("…", name);
    }
}
