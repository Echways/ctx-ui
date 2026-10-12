using System.Globalization;
using CtxUi.Core;

namespace CtxUi.Server;

public static class ExpiryArgument
{
    public const string Never = "never";

    /// <summary>
    /// Дата означает полночь UTC: с этого момента доступ закрыт.
    /// Разбор по умолчанию взял бы полночь по часам сервера и сдвинул срок на часовой пояс.
    /// </summary>
    public static DateTimeOffset? Parse(string text)
    {
        if (text == Never)
            return null;

        if (!DateTimeOffset.TryParseExact(
                text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            throw new DomainException($"дата «{text}» не разобрана: нужен вид ГГГГ-ММ-ДД, например 2027-07-01, или {Never}");

        return date;
    }
}
