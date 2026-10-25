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

    [Theory]
    [InlineData(0, "0\u00A0мс")]
    [InlineData(999, "999\u00A0мс")]
    [InlineData(1_240, "1,2\u00A0с")]
    [InlineData(12_600, "13\u00A0с")]
    public void Duration_switches_to_seconds_from_one_second(int milliseconds, string expected) =>
        Assert.Equal(expected, Words.Duration(milliseconds));

    [Theory]
    [InlineData(0, "0\u00A0Б")]
    [InlineData(1_023, "1\u00A0023\u00A0Б")]
    [InlineData(1_536, "1,5\u00A0КБ")]
    [InlineData(150 * 1_024, "150\u00A0КБ")]
    [InlineData(5L * 1_024 * 1_024 * 1_024 * 1_024, "5\u00A0120\u00A0ГБ")]
    public void Size_picks_the_largest_unit_below_1024(long bytes, string expected) => Assert.Equal(expected, Words.Size(bytes));

    [Theory]
    [InlineData(0, "0\u00A0%")]
    [InlineData(0.1234, "12,3\u00A0%")]
    [InlineData(1, "100\u00A0%")]
    public void Share_is_a_percent_with_one_decimal(double share, string expected) => Assert.Equal(expected, Words.Share(share));
}
