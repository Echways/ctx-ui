namespace CtxUi.Core.Clients;

public static class QuotaPeriod
{
    public static DateTimeOffset StartAt(DateTimeOffset anchor, int? resetEveryDays, DateTimeOffset now)
    {
        if (resetEveryDays is not { } days || now <= anchor)
            return anchor;

        var periods = (now - anchor).Ticks / (TimeSpan.TicksPerDay * days);
        return anchor.AddDays(periods * days);
    }

    public static DateTimeOffset? NextReset(DateTimeOffset anchor, int? resetEveryDays, DateTimeOffset now) =>
        resetEveryDays is { } days ? StartAt(anchor, days, now).AddDays(days) : null;

    public static long UsedCalls(Client client, DateTimeOffset now) =>
        client.UsedSince == StartAt(client.PeriodStartedAt, client.ResetEveryDays, now) ? client.UsedCalls : 0;
}
