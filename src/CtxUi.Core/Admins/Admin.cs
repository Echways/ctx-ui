namespace CtxUi.Core.Admins;

public sealed class Admin
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public string PasswordHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public int FailedAttempts { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
