namespace CtxUi.Core.Usage;

public sealed record UsageRecord(
    Guid ClientId,
    Guid InboundId,
    DateTimeOffset At,
    string? Method,
    string? Tool,
    int DurationMs,
    UsageOutcome Outcome,
    Guid? DeviceId = null)
{
    public const string ToolsCall = "tools/call";

    public int Status { get; init; }

    public int RequestBytes { get; init; }

    public long ResponseBytes { get; init; }

    public bool Forwarded => IsForwarded(Outcome);

    public DateOnly Day => DateOnly.FromDateTime(At.UtcDateTime);

    public static bool IsForwarded(UsageOutcome outcome) => outcome is
        UsageOutcome.Ok or UsageOutcome.UpstreamRejected or UsageOutcome.UpstreamFailed or UsageOutcome.ClientAborted;
}

public sealed record DailyTotal(DateOnly Day, int Calls, int ToolCalls, int Errors, int Refused, long DurationMs);
