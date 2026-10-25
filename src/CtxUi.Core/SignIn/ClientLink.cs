namespace CtxUi.Core.SignIn;

public sealed class ClientLink
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public required string Provider { get; set; }
    public required string Subject { get; set; }
    public required string Display { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}
