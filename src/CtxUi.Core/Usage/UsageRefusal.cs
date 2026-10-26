namespace CtxUi.Core.Usage;

public sealed class UsageRefusal
{
    public static readonly TimeSpan Precision = TimeSpan.FromMinutes(1);

    public DateTimeOffset Minute { get; set; }
    public Guid ClientId { get; set; }
    public Guid InboundId { get; set; }
    public UsageOutcome Outcome { get; set; }
    public int Count { get; set; }
}
