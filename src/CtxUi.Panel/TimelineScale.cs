using System.Globalization;
using Enfilade;

namespace CtxUi.Panel;

public sealed record TimelineScale(TimeSpan Length)
{
    private static readonly TimeSpan Quarter = TimeSpan.FromMinutes(15);

    public TimeSpan TickEvery => Length switch
    {
        _ when Length <= Quarter => TimeSpan.FromHours(6),
        _ when Length <= TimeSpan.FromHours(2) => TimeSpan.FromDays(1),
        _ when Length <= TimeSpan.FromHours(8) => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(15),
    };

    public string Span => Length switch
    {
        _ when Length <= Quarter => PanelText.HorizonSpanDay,
        _ when Length <= TimeSpan.FromHours(2) => PanelText.HorizonSpanWeek,
        _ when Length <= TimeSpan.FromHours(8) => PanelText.HorizonSpanMonth,
        _ => PanelText.HorizonSpanSeason,
    };

    public string Hint => Length switch
    {
        _ when Length <= Quarter => PanelText.HorizonHint,
        _ when Length <= TimeSpan.FromHours(2) => PanelText.HorizonHintWeek,
        _ when Length <= TimeSpan.FromHours(8) => PanelText.HorizonHintMonth,
        _ => PanelText.HorizonHintSeason,
    };

    public bool IsTick(DateTimeOffset start) => start.UtcTicks % TickEvery.Ticks == 0;

    public string Tick(DateTimeOffset start) => TickEvery < TimeSpan.FromDays(1) ? Words.Clock(start) : Date(start, "d MMM");

    public string Slot(DateTimeOffset start) => Length switch
    {
        _ when Length <= Quarter => string.Format(PanelText.HorizonQuarter, Words.Clock(start), Words.Clock(start + Length)),
        _ when Length < TimeSpan.FromDays(1) => string.Format(PanelText.HorizonSlot, Date(start, "d MMMM"), Words.Clock(start), Words.Clock(start + Length)),
        _ => Date(start, "d MMMM"),
    };

    private static string Date(DateTimeOffset moment, string format) => moment.UtcDateTime.ToString(format, CultureInfo.CurrentCulture);
}
