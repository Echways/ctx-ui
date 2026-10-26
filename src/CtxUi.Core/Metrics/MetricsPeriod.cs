using CtxUi.Core.Usage;

namespace CtxUi.Core.Metrics;

public sealed record MetricsPeriod(int Days, TimeSpan SlotLength)
{
    public const int DefaultDays = 7;

    public static readonly MetricsPeriod Day = new(1, TimeSpan.FromMinutes(15));

    public static readonly IReadOnlyList<MetricsPeriod> Known =
    [
        Day,
        new(7, TimeSpan.FromHours(2)),
        new(30, TimeSpan.FromHours(8)),
        new(90, TimeSpan.FromDays(1)),
    ];

    public int Slots => (int)(TimeSpan.FromDays(Days) / SlotLength);

    public static IReadOnlyList<MetricsPeriod> Within(int retentionDays)
    {
        var kept = Known.Where(period => period.Days <= retentionDays).ToList();
        return kept.Count == 0 ? [Day] : kept;
    }

    public Timeline TimelineAt(DateTimeOffset now) => Timeline.Ending(now, SlotLength, Slots);
}
