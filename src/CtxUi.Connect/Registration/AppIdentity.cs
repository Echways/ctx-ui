using System.Security.Cryptography;
using System.Text;

namespace CtxUi.Connect.Registration;

public sealed record AppIdentity(string Key, string Name, IReadOnlyList<string> RedirectHosts, bool LoopbackOnly)
{
    public const string DynamicPrefix = "dcr:";

    public string? VerifiedBy =>
        Uri.TryCreate(Key, UriKind.Absolute, out var document) && document.Scheme == Uri.UriSchemeHttps ? document.IdnHost : null;

    public static AppIdentity Of(string? documentUrl, string name, IReadOnlyCollection<string> redirectUris) => new(
        documentUrl ?? DynamicPrefix + Fingerprint(name, redirectUris),
        name,
        redirectUris.Select(Host).Distinct(StringComparer.Ordinal).ToList(),
        redirectUris.Count > 0 && redirectUris.All(RedirectUris.IsLoopback));

    public static string Fingerprint(string name, IEnumerable<string> redirectUris) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(string.Join('\n', redirectUris.Order(StringComparer.Ordinal).Prepend(name)))));

    private static string Host(string redirectUri) =>
        !RedirectUris.TryParse(redirectUri, out var uri) ? redirectUri
        : uri.Scheme is "https" or "http" ? uri.Host
        : uri.Scheme + "://" + uri.Host;
}
