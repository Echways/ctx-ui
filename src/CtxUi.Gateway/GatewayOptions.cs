namespace CtxUi.Gateway;

public sealed class GatewayOptions
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int UsageRetentionDays { get; set; } = 30;

    public TimeSpan ConcurrencyWait { get; set; } = TimeSpan.FromSeconds(1);
}
