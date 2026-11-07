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

    public bool ChangesAuth(ServerSettingsView current)
    {
        return Kind != current.AuthKind
               || (Kind == ApiAuthKind.Header && Normalized(HeaderName) != Normalized(current.AuthHeaderName))
               || (Kind == ApiAuthKind.Basic && Normalized(Username) != Normalized(current.AuthUsername))
               || (Kind != ApiAuthKind.None && !string.IsNullOrEmpty(Secret));
    }

    public bool ChangesBaseUrl(ServerSettingsView current)
    {
        return Normalized(BaseUrl).TrimEnd('/') != Normalized(current.BaseUrl).TrimEnd('/');
    }

    private static string Normalized(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
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
