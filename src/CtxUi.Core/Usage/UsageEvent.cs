namespace CtxUi.Core.Usage;

public sealed class UsageEvent
{
    public long Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid InboundId { get; set; }
    public DateTimeOffset At { get; set; }
    public required string Method { get; set; }
    public string? Tool { get; set; }
    public int DurationMs { get; set; }
    public UsageOutcome Outcome { get; set; }
}
