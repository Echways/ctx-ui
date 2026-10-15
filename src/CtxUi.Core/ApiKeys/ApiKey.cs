namespace CtxUi.Core.ApiKeys;

public sealed class ApiKey
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string NameKey { get; set; }
    public required string SecretHash { get; set; }
    public required string SecretPrefix { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
