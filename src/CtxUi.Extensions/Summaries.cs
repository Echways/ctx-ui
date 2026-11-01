namespace CtxUi.Extensions;

public sealed record ClientSummary(
    Guid Id,
    string Name,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    long UsedCalls,
    int? QuotaCalls,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record InboundSummary(Guid Id, string Slug, string Name, bool Enabled, DateTimeOffset CreatedAt);

public sealed record ApiKeySummary(Guid Id, string Name);

public sealed record UsageDay(DateOnly Day, Guid ClientId, Guid InboundId, int Calls, int ToolCalls, int Errors, int Refused, long DurationMs);
