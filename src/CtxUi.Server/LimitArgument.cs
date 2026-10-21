using System.Globalization;
using CtxUi.Core;

namespace CtxUi.Server;

public static class LimitArgument
{
    public const string None = "none";

    public static int? Parse(string option, string text)
    {
        if (text == None)
            return null;

        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new DomainException(string.Format(ServerText.LimitInvalid, option, text, None));

        return value;
    }
}
