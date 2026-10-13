namespace CtxUi.Core.Usage;

public sealed class UsageDaily
{
    public DateOnly Day { get; set; }
    public Guid ClientId { get; set; }
    public Guid InboundId { get; set; }
    public int Calls { get; set; }
    public int ToolCalls { get; set; }
    public int Errors { get; set; }
    public int Refused { get; set; }
    public long DurationMs { get; set; }
}
