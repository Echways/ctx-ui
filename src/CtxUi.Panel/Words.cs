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

    public static string Duration(int milliseconds) => milliseconds < 1_000
        ? string.Format(PanelText.Milliseconds, Number(milliseconds))
        : string.Format(PanelText.Seconds, (milliseconds / 1_000.0).ToString(milliseconds < 10_000 ? "0.#" : "N0", CultureInfo.CurrentCulture));

    public static string Size(long bytes)
    {
        string[] units = [PanelText.Bytes, PanelText.Kilobytes, PanelText.Megabytes, PanelText.Gigabytes];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1_024 && unit < units.Length - 1)
        {
            value /= 1_024;
            unit++;
        }

        return string.Format(units[unit], value.ToString(unit == 0 || value >= 100 ? "N0" : "0.#", CultureInfo.CurrentCulture));
    }

    public static string Share(double share) =>
        string.Format(PanelText.Percent, Math.Round(100 * share, 1).ToString("0.#", CultureInfo.CurrentCulture));

    public static string OrDash(long value) => value == 0 ? "–" : Number(value);

    public static string Count(string pattern, long count) => MessageFormatter.Format(
        pattern, new Dictionary<string, object?> { ["n"] = count, ["number"] = Number(count) }, CultureInfo.CurrentUICulture);
}
