using System.Globalization;

namespace CtxUi.Gateway;

public static class QuotaRefusal
{
    private static readonly string[] Months =
    [
        "января", "февраля", "марта", "апреля", "мая", "июня",
        "июля", "августа", "сентября", "октября", "ноября", "декабря",
    ];

    public static string Text(QuotaCharge charge, DateTimeOffset now)
    {
        if (charge.ResetsAt is not { } resetsAt)
            return $"Лимит вызовов исчерпан: {charge.Limit}. Новых вызовов не будет, обратитесь к тому, кто выдал доступ.";

        var moment = resetsAt.ToUniversalTime();
        var year = moment.Year == now.ToUniversalTime().Year ? "" : $" {moment.Year} года";
        var time = moment.ToString("HH:mm", CultureInfo.InvariantCulture);
        return $"Лимит вызовов исчерпан: {charge.Limit} за период. Обновится {moment.Day} {Months[moment.Month - 1]}{year} в {time} UTC.";
    }
}
