using CtxUi.Core;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public class ExpiryArgumentTests
{
    [Fact]
    public void Date_means_midnight_utc_whatever_the_server_time_zone_is()
    {
        var parsed = ExpiryArgument.Parse("2027-07-01");

        Assert.Equal(new DateTimeOffset(2027, 7, 1, 0, 0, 0, TimeSpan.Zero), parsed);
        Assert.Equal(TimeSpan.Zero, parsed!.Value.Offset);
    }

    [Fact]
    public void Never_means_no_expiry() => Assert.Null(ExpiryArgument.Parse("never"));

    [Theory]
    [InlineData("01.07.2027")]
    [InlineData("2027-7-1")]
    [InlineData("2027-07-01T10:00")]
    [InlineData("2027-02-30")]
    [InlineData("")]
    public void Anything_else_is_rejected_with_the_expected_format(string text)
    {
        var error = Assert.Throws<DomainException>(() => ExpiryArgument.Parse(text));

        Assert.Contains("ГГГГ-ММ-ДД", error.Message);
    }
}
