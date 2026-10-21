namespace CtxUi.Panel.Tests;

public class WordsTests
{
    private const string Clients =
        "{n, plural, one {{number} клиент} few {{number} клиента} many {{number} клиентов} other {{number} клиента}}";

    private static readonly DateTimeOffset Moment = new(2026, 10, 12, 2, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Middle_keeps_short_text_whole() => Assert.Equal("abc", Words.Middle("abc", 10));

    [Fact]
    public void Middle_cuts_long_text_in_the_middle() =>
        Assert.Equal("tnk_AB…XYZ", Words.Middle("tnk_ABCDEFGHIJKLMNOPQRSTUVWXYZ", 10));

    [Theory]
    [InlineData(1, "1 клиент")]
    [InlineData(2, "2 клиента")]
    [InlineData(5, "5 клиентов")]
    [InlineData(21, "21 клиент")]
    public void Count_uses_russian_plural_forms(long count, string expected) => Assert.Equal(expected, Words.Count(Clients, count));

    [Fact]
    public void Number_groups_thousands_the_russian_way() => Assert.Equal("1 000", Words.Number(1000));

    [Fact]
    public void Day_minute_and_clock_are_in_utc()
    {
        Assert.Equal("2026-10-11", Words.Day(Moment));
        Assert.Equal("2026-10-11 23:30", Words.Minute(Moment));
        Assert.Equal("23:30", Words.Clock(Moment));
    }
}
