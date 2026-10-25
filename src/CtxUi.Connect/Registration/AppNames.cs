using System.Text.RegularExpressions;

namespace CtxUi.Connect.Registration;

public static partial class AppNames
{
    public const int MaxLength = 100;

    public static string Clean(string? given)
    {
        var name = Separators().Replace(given ?? "", " ").Trim();
        return name.Length <= MaxLength ? name : name[..MaxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[\s\p{C}]+")]
    private static partial Regex Separators();
}
