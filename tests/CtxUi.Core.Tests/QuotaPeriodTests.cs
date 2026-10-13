using CtxUi.Core.Clients;

namespace CtxUi.Core.Tests;

public class QuotaPeriodTests
{
    private static readonly DateTimeOffset Anchor = TestData.Now;

    [Fact]
    public void Without_a_reset_the_period_never_ends()
    {
        Assert.Equal(Anchor, QuotaPeriod.StartAt(Anchor, null, Anchor.AddYears(5)));
        Assert.Null(QuotaPeriod.NextReset(Anchor, null, Anchor.AddYears(5)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(29, 0)]
    [InlineData(30, 30)]
    [InlineData(31, 30)]
    [InlineData(95, 90)]
    public void Period_starts_on_a_whole_number_of_reset_intervals_from_the_anchor(int daysPassed, int expectedStartDay)
    {
        var start = QuotaPeriod.StartAt(Anchor, 30, Anchor.AddDays(daysPassed));

        Assert.Equal(Anchor.AddDays(expectedStartDay), start);
    }

    [Fact]
    public void New_period_begins_exactly_at_the_boundary()
    {
        var boundary = Anchor.AddDays(7);

        Assert.Equal(Anchor, QuotaPeriod.StartAt(Anchor, 7, boundary.AddMilliseconds(-1)));
        Assert.Equal(boundary, QuotaPeriod.StartAt(Anchor, 7, boundary));
    }

    [Fact]
    public void Next_reset_is_one_interval_after_the_current_start()
    {
        Assert.Equal(Anchor.AddDays(30), QuotaPeriod.NextReset(Anchor, 30, Anchor.AddDays(12)));
        Assert.Equal(Anchor.AddDays(60), QuotaPeriod.NextReset(Anchor, 30, Anchor.AddDays(30)));
    }

    [Fact]
    public void Moment_before_the_anchor_belongs_to_the_first_period()
    {
        Assert.Equal(Anchor, QuotaPeriod.StartAt(Anchor, 30, Anchor.AddDays(-2)));
        Assert.Equal(Anchor.AddDays(30), QuotaPeriod.NextReset(Anchor, 30, Anchor.AddDays(-2)));
    }

    [Fact]
    public void Stored_counter_counts_only_while_its_period_is_current()
    {
        var client = TestData.Client("Иванов");
        client.ResetEveryDays = 30;
        client.PeriodStartedAt = Anchor;
        client.UsedSince = Anchor;
        client.UsedCalls = 57;

        Assert.Equal(57, QuotaPeriod.UsedCalls(client, Anchor.AddDays(29)));
        Assert.Equal(0, QuotaPeriod.UsedCalls(client, Anchor.AddDays(30)));
    }

    [Fact]
    public void Counter_never_written_reads_as_zero()
    {
        var client = TestData.Client("Иванов");

        Assert.Equal(0, QuotaPeriod.UsedCalls(client, Anchor));
    }
}
