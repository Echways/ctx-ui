namespace CtxUi.Core.SignIn;

public static class OAuthAvailability
{
    private static readonly HashSet<string> LoopbackHosts = new(StringComparer.OrdinalIgnoreCase) { "localhost", "127.0.0.1", "[::1]" };

    public static bool IsAllowed(string? publicUrl) =>
        Uri.TryCreate(publicUrl, UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && LoopbackHosts.Contains(url.Host)));
}
