using CtxUi.Builder.Contracts;

namespace CtxUi.Builder.Servers;

public enum ApiAuthKind { None, Bearer, Header, Basic }

public sealed record ApiAuthInput(ApiAuthKind Kind, string? HeaderName = null, string? Username = null, string? Secret = null);

public sealed record ServerSettingsView(
    Guid ServerId,
    string Slug,
    string Name,
    NetworkAccess Access,
    ServerLimits Limits,
    string? BaseUrl,
    ApiAuthKind AuthKind,
    string? AuthHeaderName,
    string? AuthUsername,
    bool AuthHasSecret,
    string Dependencies,
    long SettingsRevision,
    IReadOnlyList<string> SecretNames);

public sealed record ProjectExport(string FileName, byte[] Content);
