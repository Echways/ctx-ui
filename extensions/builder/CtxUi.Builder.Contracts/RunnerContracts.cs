namespace CtxUi.Builder.Contracts;

public enum NetworkAccess { InternetOnly, FullNetwork }

public sealed record ServerLimits(int MemoryMb = 512, double Cpus = 1, int Pids = 256);

public sealed record ServerSpec(NetworkAccess Network, ServerLimits Limits, string RouteSecret);

public enum ReleaseState { Building, Built, Failed, Candidate, Active, Retired }

public sealed record ReleaseStatus(
    int Number,
    ReleaseState State,
    string Log,
    string? LockFile,
    string? EnvironmentHash,
    string? Error);

public sealed record StartRelease(IReadOnlyDictionary<string, string> Environment);

public sealed record RunnerHealth(string Version, string DockerVersion, bool EgressReachable);

public sealed record ServerState(Guid Id, int? ActiveRelease, IReadOnlyList<ReleaseStatus> Releases);
