using CtxUi.Builder.Contracts;

namespace CtxUi.Builder;

public sealed record ServerSummaryModel(Guid Id, string Slug, string Name, string NetworkAccess, string State, int? ActiveRelease, int? BuildingRelease, string? InboundId);

public sealed record ReleaseModel(
    int Number,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool IsActive,
    bool CanRollback,
    string? Error,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> RemovedTools);

public sealed record ServerDetailsModel(
    Guid Id,
    string Slug,
    string Name,
    string NetworkAccess,
    ServerLimits Limits,
    string? BaseUrl,
    string State,
    int? ActiveRelease,
    int? BuildingRelease,
    IReadOnlyList<string> SecretNames,
    IReadOnlyList<ReleaseModel> Releases);

public sealed record ReleaseStartedModel(int Release);
