namespace CtxUi.Panel.Tests;

public class TimelineScaleTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Quarters_are_marked_every_six_hours_by_the_clock()
    {
        var scale = new TimelineScale(TimeSpan.FromMinutes(15));

        Assert.True(scale.IsTick(Midnight.AddHours(6)));
        Assert.False(scale.IsTick(Midnight.AddHours(7)));
        Assert.Equal("06:00", scale.Tick(Midnight.AddHours(6)));
        Assert.Equal("06:00–06:15 UTC", scale.Slot(Midnight.AddHours(6)));
        Assert.Equal("Последние сутки, по 15 минут на штрих", scale.Hint);
    }

    [Fact]
    public void Two_hour_strokes_are_marked_by_day_and_read_with_the_date()
    {
        var scale = new TimelineScale(TimeSpan.FromHours(2));

        Assert.True(scale.IsTick(Midnight));
        Assert.False(scale.IsTick(Midnight.AddHours(2)));
        Assert.Equal("12 окт.", scale.Tick(Midnight));
        Assert.Equal("12 октября, 02:00–04:00 UTC", scale.Slot(Midnight.AddHours(2)));
        Assert.Equal("за последние 7 дней по 2 часа", scale.Span);
    }

    [Fact]
    public void Day_strokes_read_as_a_date()
    {
        var scale = new TimelineScale(TimeSpan.FromDays(1));

        Assert.Equal("12 октября", scale.Slot(Midnight));
        Assert.Equal(TimeSpan.FromDays(15), scale.TickEvery);
    }
}
