namespace CtxUi.Core;

public static class PairLines
{
    public static List<KeyValuePair<string, string>> Parse(IEnumerable<string>? lines)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var line in lines ?? [])
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new DomainException(string.Format(CoreText.PairLineInvalid, line));

            pairs.Add(KeyValuePair.Create(line[..separator], line[(separator + 1)..]));
        }

        return pairs;
    }

    public static List<KeyValuePair<string, string>> ParseText(string? text) =>
        Parse((text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
