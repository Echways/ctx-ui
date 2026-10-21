using System.Globalization;
using CtxUi.Core;

namespace CtxUi.Server;

public static class ExpiryArgument
{
    public const string Never = "never";

    public static DateTimeOffset? Parse(string text)
    {
        if (text == Never)
            return null;

        if (!DateTimeOffset.TryParseExact(
                text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            throw new DomainException(string.Format(ServerText.ExpiryInvalid, text, Never));

        return date;
    }
}
