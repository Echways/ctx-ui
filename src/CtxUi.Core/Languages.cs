namespace CtxUi.Core;

public static class Languages
{
    public const string Default = "en";

    public static IReadOnlyList<string> Supported { get; } = [Default, "ru"];

    public static bool IsSupported(string? name)
    {
        return Supported.Contains(name, StringComparer.Ordinal);
    }
}
