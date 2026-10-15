using System.Globalization;

namespace CtxUi.Panel;

public static class Words
{
    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string Day(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Minute(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Second(DateTimeOffset moment) => moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
