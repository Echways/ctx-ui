using System.Globalization;
using Jeffijoe.MessageFormat;

namespace CtxUi.Panel;

public static class Words
{
    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string Day(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Minute(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Clock(DateTimeOffset moment, bool seconds = false) =>
        moment.UtcDateTime.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.InvariantCulture);

    public static string Middle(string text, int length) =>
        text.Length <= length ? text : text[..(length * 3 / 5)] + "…" + text[^(length * 2 / 5 - 1)..];

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string OrDash(long value) => value == 0 ? "–" : Number(value);

    public static string Count(string pattern, long count) => MessageFormatter.Format(
        pattern, new Dictionary<string, object?> { ["n"] = count, ["number"] = Number(count) }, CultureInfo.CurrentUICulture);

    public static string Second(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
