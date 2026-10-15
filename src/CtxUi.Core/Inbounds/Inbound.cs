namespace CtxUi.Core.Inbounds;

public sealed class Inbound
{
    public const int DefaultRequestTimeoutSeconds = 300;
    public const int RequestTimeoutCeiling = 3_600;
    public const int DefaultMaxBodyBytes = 4 * 1024 * 1024;
    public const int MaxBodyBytesFloor = 1_024;
    public const int MaxBodyBytesCeiling = 64 * 1024 * 1024;

    public Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public bool Enabled { get; set; } = true;
    public InboundKind Kind { get; set; }
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;
    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;
    public string? Url { get; set; }

    public string? UpstreamHeadersSealed { get; set; }

    public bool ForwardIdentity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
