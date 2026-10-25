using UAParser;

namespace CtxUi.Connect.SignIn;

public static class DeviceNames
{
    private const string Unrecognized = "Other";

    private static readonly Parser Parser = Parser.GetDefault();

    public static string From(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return ConnectText.UnknownDevice;

        var info = Parser.Parse(userAgent);
        var parts = new[] { info.UA.Family, info.OS.Family }.Where(part => part is not (null or "" or Unrecognized)).ToList();
        return parts.Count == 0 ? ConnectText.UnknownDevice : string.Join(" · ", parts);
    }
}
