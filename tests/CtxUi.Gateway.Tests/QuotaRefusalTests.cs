using System.Globalization;

namespace CtxUi.Gateway.Tests;

public class QuotaRefusalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static QuotaCharge Refused(DateTimeOffset? resetsAt) => new(false, Now, 1, 100, resetsAt);

    [Fact]
    public void Text_names_the_limit_and_the_day_it_comes_back() =>
        Assert.Equal(
            "Лимит вызовов исчерпан: 100 за период. Обновится 1 ноября в 10:00 UTC.",
            QuotaRefusal.Text(Refused(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero)), Now));

    [Fact]
    public void Reset_in_another_year_is_named_with_the_year() =>
        Assert.Equal(
            "Лимит вызовов исчерпан: 100 за период. Обновится 3 января 2027 года в 00:05 UTC.",
            QuotaRefusal.Text(Refused(new DateTimeOffset(2027, 1, 3, 0, 5, 0, TimeSpan.Zero)), Now));

    [Fact]
    public void Reset_time_is_shown_in_utc_whatever_offset_it_was_computed_in() =>
        Assert.Contains(
            "Обновится 31 октября в 22:30 UTC.",
            QuotaRefusal.Text(Refused(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(3))), Now));

    [Fact]
    public void Refusal_reads_in_english_when_the_culture_is_english()
    {
        var (culture, uiCulture) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try
        {
            Assert.Equal(
                "Call limit reached: 100 per period. Resets on October 19 at 10:00 UTC.",
                QuotaRefusal.Text(Refused(new DateTimeOffset(2026, 10, 19, 10, 0, 0, TimeSpan.Zero)), Now));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (culture, uiCulture);
        }
    }

    [Fact]
    public void Quota_without_a_reset_says_whom_to_ask() =>
        Assert.Equal(
            "Лимит вызовов исчерпан: 100. Новых вызовов не будет, обратитесь к тому, кто выдал доступ.",
            QuotaRefusal.Text(Refused(null), Now));

    [Theory]
    [InlineData(1, "января")]
    [InlineData(2, "февраля")]
    [InlineData(3, "марта")]
    [InlineData(4, "апреля")]
    [InlineData(5, "мая")]
    [InlineData(6, "июня")]
    [InlineData(7, "июля")]
    [InlineData(8, "августа")]
    [InlineData(9, "сентября")]
    [InlineData(10, "октября")]
    [InlineData(11, "ноября")]
    [InlineData(12, "декабря")]
    public void Month_is_written_the_way_people_say_dates(int month, string expected) =>
        Assert.Contains(
            $"Обновится 15 {expected} в",
            QuotaRefusal.Text(Refused(new DateTimeOffset(2026, month, 15, 0, 0, 0, TimeSpan.Zero)), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
}
