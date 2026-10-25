using System.Diagnostics.CodeAnalysis;

namespace CtxUi.Connect.Registration;

public static class RedirectUris
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "[::1]" };

    private static readonly HashSet<string> ForbiddenSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp, "javascript", "data", Uri.UriSchemeFile, "vbscript", "blob", "about",
    };

    public static bool TryParse(string? uri, [NotNullWhen(true)] out Uri? parsed) =>
        Uri.TryCreate(uri, UriKind.Absolute, out parsed) && uri!.StartsWith(parsed.Scheme + ":", StringComparison.OrdinalIgnoreCase);

    public static bool IsLoopback(string uri) => TryParse(uri, out var parsed) && IsLoopback(parsed);

    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
            return true;

        return TryParse(registered, out var expected)
            && TryParse(requested, out var actual)
            && IsLoopback(expected)
            && IsLoopback(actual)
            && string.Equals(expected.Host, actual.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(expected.PathAndQuery, actual.PathAndQuery, StringComparison.Ordinal);
    }

    public static bool IsAllowedForRegistration(string uri)
    {
        if (!TryParse(uri, out var parsed) || uri.Contains('#'))
            return false;

        return parsed.Scheme == Uri.UriSchemeHttps
            || IsLoopback(parsed)
            || !ForbiddenSchemes.Contains(parsed.Scheme);
    }

    private static bool IsLoopback(Uri uri) => uri.Scheme == Uri.UriSchemeHttp && LoopbackHosts.Contains(uri.Host);
}
