using CtxUi.Builder.Contracts;
using CtxUi.Builder.Servers;

namespace CtxUi.Builder.Components;

public sealed class ApiForm
{
    public string? BaseUrl { get; set; }

    public ApiAuthKind Kind { get; set; }

    public string? HeaderName { get; set; }

    public string? Username { get; set; }

    public string? Secret { get; set; }

    public bool ChangesAuth(ServerSettingsView current) =>
        Kind != current.AuthKind
        || (Kind == ApiAuthKind.Header && HeaderName?.Trim() != current.AuthHeaderName)
        || (Kind == ApiAuthKind.Basic && Username?.Trim() != current.AuthUsername)
        || (Kind != ApiAuthKind.None && !string.IsNullOrEmpty(Secret));

    public bool ChangesBaseUrl(ServerSettingsView current) => Normalized(BaseUrl) != Normalized(current.BaseUrl);

    private static string Normalized(string? address) => (address ?? string.Empty).Trim().TrimEnd('/');
}

public sealed class DependenciesForm
{
    public string? Text { get; set; }
}

public sealed class SecretForm
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

public sealed class AccessForm
{
    public NetworkAccess Access { get; set; }

    public int MemoryMb { get; set; }

    public double Cpus { get; set; }

    public int Pids { get; set; }
}
