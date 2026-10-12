namespace CtxUi.Gateway;

public sealed class GatewayOptions
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(1);
}
