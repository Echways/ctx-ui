using CtxUi.Core;

namespace CtxUi.Server.Tests;

public class LimitArgumentTests
{
    [Fact]
    public void None_removes_the_limit() => Assert.Null(LimitArgument.Parse("--rate", "none"));

    [Fact]
    public void Whole_number_is_the_limit() => Assert.Equal(12, LimitArgument.Parse("--rate", "12"));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("12 ")]
    [InlineData("abc")]
    public void Anything_else_is_refused(string text) =>
        Assert.Throws<DomainException>(() => LimitArgument.Parse("--rate", text));
}
