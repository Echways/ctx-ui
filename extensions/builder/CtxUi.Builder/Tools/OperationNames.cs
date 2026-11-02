using System.Text.RegularExpressions;

namespace CtxUi.Builder.Tools;

internal static partial class OperationNames
{
    private const int MaxLength = OpenApiDraft.MaxNameLength;

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex Forbidden();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWord();

    public static string Unique(string? declared, string method, string path, HashSet<string> used)
    {
        var name = Normalized(declared);
        if (name.Length == 0)
            name = Normalized(Generated(method, path));

        var candidate = name;
        for (var attempt = 2; !used.Add(candidate); attempt++)
        {
            var suffix = "_" + attempt;
            candidate = name[..Math.Min(name.Length, MaxLength - suffix.Length)].TrimEnd('_') + suffix;
        }

        return candidate;
    }

    private static string Normalized(string? raw)
    {
        var name = Forbidden().Replace(raw ?? string.Empty, "_").Trim('_');
        return name.Length > MaxLength ? name[..MaxLength].TrimEnd('_') : name;
    }

    private static string Generated(string method, string path)
    {
        var tail = NotWord().Replace(path.ToLowerInvariant(), "_").Trim('_');
        return tail.Length == 0 ? method.ToLowerInvariant() : $"{method.ToLowerInvariant()}_{tail}";
    }
}
