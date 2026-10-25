namespace CtxUi.Core.SignIn;

public sealed class DeviceApp
{
    public Guid DeviceId { get; set; }
    public required string AppKey { get; set; }
    public required string AppName { get; set; }
    public required string AuthorizationId { get; set; }
    public DateTimeOffset ConnectedAt { get; set; }
}
