namespace CtxUi.Core.Usage;

public sealed record Slot(DateTimeOffset Start, int Calls, int Failures);

public sealed record Timeline(DateTimeOffset From, TimeSpan Length, int Count)
{
    public DateTimeOffset Until => From + Length * Count;

    public static DateTimeOffset Floor(DateTimeOffset moment, TimeSpan length) =>
        new(moment.UtcTicks - moment.UtcTicks % length.Ticks, TimeSpan.Zero);

    public static Timeline Ending(DateTimeOffset now, TimeSpan length, int count) =>
        new(Floor(now, length) - length * (count - 1), length, count);

    public DateTimeOffset StartOf(int slot) => From + Length * slot;
}
