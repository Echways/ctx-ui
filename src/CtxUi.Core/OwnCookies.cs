namespace CtxUi.Core;

public static class OwnCookies
{
    public const string Session = "ctx-ui";

    private const string SessionChunk = Session + "C";

    private const string FrameworkPrefix = ".AspNetCore.";

    public static bool Contains(string name) =>
        name == Session
        || IsSessionChunk(name)
        || name.StartsWith(FrameworkPrefix, StringComparison.Ordinal);

    private static bool IsSessionChunk(string name) =>
        name.Length > SessionChunk.Length
        && name.StartsWith(SessionChunk, StringComparison.Ordinal)
        && !name.AsSpan(SessionChunk.Length).ContainsAnyExceptInRange('0', '9');
}
