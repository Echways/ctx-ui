namespace CtxUi.Core.Inbounds;

public sealed class Inbound
{
    public Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public InboundKind Kind { get; set; }
    public int RequestTimeoutSeconds { get; set; } = 300;
    public string? Url { get; set; }

    /// <summary>Зашифрованный JSON с заголовками для upstream или null, если заголовков нет.</summary>
    public string? UpstreamHeadersSealed { get; set; }

    public bool ForwardIdentity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
