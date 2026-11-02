using System.Text.RegularExpressions;

namespace CtxUi.Builder.Tools;

public static partial class CodeToolNames
{
    [GeneratedRegex(@"^[ \t]*@tool(?:\((?<args>[^)]*)\))?[ \t]*\r?\n(?:[ \t]*@[^\n]*\n)*[ \t]*(?:async[ \t]+)?def[ \t]+(?<function>\w+)", RegexOptions.Multiline)]
    private static partial Regex Decorated();

    [GeneratedRegex(@"(?:\bname\s*=\s*|^\s*)(?:""(?<name>[^""]+)""|'(?<name>[^']+)')")]
    private static partial Regex ExplicitName();

    public static IReadOnlySet<string> From(IEnumerable<(string Path, string Content)> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, content) in files.Where(file => file.Path.StartsWith("mcp/", StringComparison.Ordinal) && file.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match match in Decorated().Matches(content))
            {
                var explicitName = ExplicitName().Match(match.Groups["args"].Value);
                names.Add(explicitName.Success ? explicitName.Groups["name"].Value : match.Groups["function"].Value);
            }
        }

        return names;
    }
}
