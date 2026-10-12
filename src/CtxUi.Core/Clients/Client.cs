namespace CtxUi.Core.Clients;

public sealed class Client
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public bool Enabled { get; set; } = true;
    public required string TokenHash { get; set; }
    public required string TokenSealed { get; set; }
    public required string TokenPrefix { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? FirstUsedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public List<ClientInbound> Inbounds { get; set; } = [];
}
