namespace CtxUi.Gateway;

public sealed class GatewayOptions
{
    public string PublicUrl { get; set; } = "http://localhost:8080";

    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ConcurrencyWait { get; set; } = TimeSpan.FromSeconds(1);
}
