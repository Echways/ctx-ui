namespace CtxUi.Builder.Runner;

public sealed class RunnerSettings
{
    public RunnerSettings(string? url, string? ingressUrl, string? token)
    {
        Url = Absolute(url);
        IngressUrl = Absolute(ingressUrl);
        Token = string.IsNullOrWhiteSpace(token) ? null : token;
        Missing = MissingNames(Url is null, IngressUrl is null, Token is null);
    }

    public Uri? Url { get; }

    public Uri? IngressUrl { get; }

    public string? Token { get; }

    public IReadOnlyList<string> Missing { get; }

    public bool IsComplete => Missing.Count == 0;

    private static string[] MissingNames(bool url, bool ingressUrl, bool token) =>
        [.. new[] { (url, "RunnerUrl"), (ingressUrl, "RunnerIngressUrl"), (token, "RunnerToken") }.Where(setting => setting.Item1).Select(setting => setting.Item2)];

    private static Uri? Absolute(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null;
}
