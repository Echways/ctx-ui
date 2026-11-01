using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Data;

public sealed class BuilderServer
{
    public Guid Id { get; set; }

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public NetworkAccess NetworkAccess { get; set; }

    public int MemoryMb { get; set; }

    public double CpuLimit { get; set; }

    public int PidsLimit { get; set; }

    public string? InboundId { get; set; }

    public Guid? ActiveReleaseId { get; set; }

    public required string RouteSecretSealed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ServerDraft
{
    public Guid ServerId { get; set; }

    public string? OpenApiJson { get; set; }

    public string? BuilderJson { get; set; }

    public string? Dependencies { get; set; }

    public string? BaseUrl { get; set; }

    public string? ApiAuthSealed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public long Revision { get; set; }

    public List<DraftFile> Files { get; set; } = [];
}

public sealed class DraftFile
{
    public Guid ServerId { get; set; }

    public required string Path { get; set; }

    public required string Content { get; set; }

    public long Revision { get; set; }
}

public sealed class ServerSecret
{
    public Guid ServerId { get; set; }

    public required string Name { get; set; }

    public required string ValueSealed { get; set; }
}

public sealed class Release
{
    public Guid Id { get; set; }

    public Guid ServerId { get; set; }

    public int Number { get; set; }

    public ReleaseState Status { get; set; }

    public string? FastMcpVersion { get; set; }

    public string? ToolsSnapshotJson { get; set; }

    public string? BuildLog { get; set; }

    public string? LockFile { get; set; }

    public string? ArchiveHash { get; set; }

    public string? EnvironmentHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string? Error { get; set; }
}
