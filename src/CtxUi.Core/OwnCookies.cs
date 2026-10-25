namespace CtxUi.Core;

public static class OwnCookies
{
    public const string Session = "ctx-ui";

    public const string ConnectSession = "ctx-ui-connect";

    public const string DeviceMarker = "ctx-ui-device";

    public const string External = "ctx-ui-external";

    private const string FrameworkPrefix = ".AspNetCore.";

    private static readonly string[] Chunked = [Session, ConnectSession, DeviceMarker, External];

    public static bool Contains(string name) =>
        Chunked.Any(own => name == own || IsChunk(name, own + "C"))
        || name.StartsWith(FrameworkPrefix, StringComparison.Ordinal);

    private static bool IsChunk(string name, string chunk) =>
        name.Length > chunk.Length
        && name.StartsWith(chunk, StringComparison.Ordinal)
        && !name.AsSpan(chunk.Length).ContainsAnyExceptInRange('0', '9');
}
