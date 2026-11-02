using System.Text.RegularExpressions;

namespace CtxUi.Builder.Tools;

internal static partial class OperationNames
{
    private const int MaxLength = 64;

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex Forbidden();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWord();

    public static string Unique(string? declared, string method, string path, HashSet<string> used)
    {
        var name = string.IsNullOrWhiteSpace(declared) ? Generated(method, path) : Forbidden().Replace(declared.Trim(), "_").Trim('_');
        if (name.Length == 0)
            name = Generated(method, path);
        if (name.Length > MaxLength)
            name = name[..MaxLength];

        var candidate = name;
        for (var attempt = 2; !used.Add(candidate); attempt++)
        {
            var suffix = "_" + attempt;
            candidate = name[..Math.Min(name.Length, MaxLength - suffix.Length)] + suffix;
        }

        return candidate;
    }

    private static string Generated(string method, string path)
    {
        var tail = NotWord().Replace(path.ToLowerInvariant(), "_").Trim('_');
        return tail.Length == 0 ? method.ToLowerInvariant() : $"{method.ToLowerInvariant()}_{tail}";
    }
}
