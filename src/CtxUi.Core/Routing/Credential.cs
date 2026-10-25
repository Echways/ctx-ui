using CtxUi.Core.SignIn;

namespace CtxUi.Core.Routing;

public enum CredentialKind
{
    Missing,
    Key,
    Grant,
    Invalid,
}

public readonly record struct Credential(CredentialKind Kind, string? Token = null, OAuthGrant? Grant = null)
{
    public static Credential FromKey(string? token) =>
        string.IsNullOrEmpty(token) ? new Credential(CredentialKind.Missing) : new Credential(CredentialKind.Key, token);
}
