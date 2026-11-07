using System.Text.RegularExpressions;

namespace CtxUi.Builder.Tools;

public static partial class CodeToolNames
{
    [GeneratedRegex(@"^[ \t]*@[ \t]*(?<dotted>[A-Za-z_]\w*(?:[ \t]*\.[ \t]*[A-Za-z_]\w*)*)", RegexOptions.Multiline)]
    private static partial Regex Decorator();

    [GeneratedRegex(@"^[ \t]*(?:async[ \t]+)?def[ \t]+(?<function>\w+)", RegexOptions.Multiline)]
    private static partial Regex Function();

    [GeneratedRegex(@"^[ \t]*from[ \t]+fastmcp(?:\.tools)?[ \t]+import[ \t]+(?<names>\([^)]*\)|[^\n]*)",
        RegexOptions.Multiline)]
    private static partial Regex FromImport();

    [GeneratedRegex(@"\btool[ \t\r\n]+as[ \t\r\n]+(?<alias>\w+)")]
    private static partial Regex Alias();

    [GeneratedRegex(@"^\s*(?<keyword>[A-Za-z_]\w*)\s*=(?!=)(?<value>.*)$", RegexOptions.Singleline)]
    private static partial Regex Keyword();

    [GeneratedRegex(@"^\s*[A-Za-z]{0,2}(?<quote>""""""|'''|""|')(?<text>.*)\k<quote>\s*$", RegexOptions.Singleline)]
    private static partial Regex Literal();

    public static IReadOnlySet<string> From(IEnumerable<(string Path, string Content)> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, content) in files.Where(file =>
                     file.Path.StartsWith("mcp/", StringComparison.Ordinal) &&
                     file.Path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)))
            Collect(content, names);
        return names;
    }

    private static void Collect(string content, HashSet<string> names)
    {
        var aliases = FromImport().Matches(content).SelectMany(match => Alias().Matches(match.Groups["names"].Value))
            .Select(match => match.Groups["alias"].Value).ToHashSet();
        foreach (Match decorator in Decorator().Matches(content))
        {
            var dotted = decorator.Groups["dotted"].Value.Replace(" ", string.Empty).Replace("\t", string.Empty);
            var last = dotted[(dotted.LastIndexOf('.') + 1)..];
            if (last != "tool" && !aliases.Contains(last))
                continue;

            var arguments = Arguments(content, decorator.Index + decorator.Length, out var end);
            var function = Function().Match(content, end);
            if (!function.Success)
                continue;
            names.Add(arguments is null
                ? function.Groups["function"].Value
                : ExplicitName(arguments) ?? function.Groups["function"].Value);
        }
    }

    private static string? ExplicitName(string arguments)
    {
        var parts = Split(arguments);
        foreach (var part in parts)
        {
            var keyword = Keyword().Match(part);
            if (keyword.Success && keyword.Groups["keyword"].Value == "name")
                return LiteralOf(keyword.Groups["value"].Value);
        }

        return parts.Count > 0 && !Keyword().IsMatch(parts[0]) ? LiteralOf(parts[0]) : null;
    }

    private static string? LiteralOf(string text)
    {
        var match = Literal().Match(text);
        return match.Success && match.Groups["text"].Value.Length > 0 ? match.Groups["text"].Value : null;
    }

    private static string? Arguments(string text, int index, out int end)
    {
        end = index;
        while (index < text.Length && text[index] is ' ' or '\t')
            index++;
        if (index >= text.Length || text[index] != '(')
            return null;

        var depth = 0;
        for (var cursor = index; cursor < text.Length; cursor++)
            switch (text[cursor])
            {
                case '"' or '\'':
                    cursor = SkipString(text, cursor) - 1;
                    break;
                case '#':
                    cursor = text.IndexOf('\n', cursor) is var newline and >= 0 ? newline - 1 : text.Length;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    if (--depth == 0)
                    {
                        end = cursor + 1;
                        return text[(index + 1)..cursor];
                    }

                    break;
            }

        end = text.Length;
        return null;
    }

    private static List<string> Split(string arguments)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var cursor = 0; cursor < arguments.Length; cursor++)
            switch (arguments[cursor])
            {
                case '"' or '\'':
                    cursor = SkipString(arguments, cursor) - 1;
                    break;
                case '#':
                    cursor = arguments.IndexOf('\n', cursor) is var newline and >= 0 ? newline : arguments.Length;
                    break;
                case '(' or '[' or '{':
                    depth++;
                    break;
                case ')' or ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(arguments[start..cursor]);
                    start = cursor + 1;
                    break;
            }

        if (arguments[start..].Trim().Length > 0)
            parts.Add(arguments[start..]);
        return parts;
    }

    private static int SkipString(string text, int index)
    {
        var quote = text[index];
        var triple = index + 2 < text.Length && text[index + 1] == quote && text[index + 2] == quote;
        var cursor = index + (triple ? 3 : 1);
        while (cursor < text.Length)
        {
            if (text[cursor] == '\\')
            {
                cursor += 2;
                continue;
            }

            if (text[cursor] == quote &&
                (!triple || (cursor + 2 < text.Length && text[cursor + 1] == quote && text[cursor + 2] == quote)))
                return cursor + (triple ? 3 : 1);
            if (!triple && text[cursor] == '\n')
                return cursor;
            cursor++;
        }

        return text.Length;
    }
}
