namespace CtxUi.Core.SignIn;

public sealed class Device
{
    public const string ByKey = "key";

    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public required string MarkerHash { get; set; }
    public required string Name { get; set; }
    public required string SignInIp { get; set; }
    public required string SignedInWith { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DeviceRevocation? RevokedReason { get; set; }
    public List<DeviceApp> Apps { get; set; } = [];
}
