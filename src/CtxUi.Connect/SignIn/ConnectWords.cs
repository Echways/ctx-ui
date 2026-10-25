using System.Globalization;
using Jeffijoe.MessageFormat;

namespace CtxUi.Connect.SignIn;

public static class ConnectWords
{
    public static string Count(string pattern, long count) => MessageFormatter.Format(
        pattern,
        new Dictionary<string, object?> { ["n"] = count, ["number"] = count.ToString("N0", CultureInfo.CurrentCulture) },
        CultureInfo.CurrentUICulture);

    public static string Moment(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}
