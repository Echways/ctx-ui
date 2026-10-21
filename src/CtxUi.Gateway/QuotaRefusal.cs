using System.Globalization;

namespace CtxUi.Gateway;

public static class QuotaRefusal
{
    public static string Text(QuotaCharge charge, DateTimeOffset now)
    {
        if (charge.ResetsAt is not { } resetsAt)
            return string.Format(GatewayText.QuotaExhaustedForGood, charge.Limit);

        var moment = resetsAt.ToUniversalTime();
        var dayFormat = moment.Year == now.ToUniversalTime().Year ? GatewayText.ResetDayFormat : GatewayText.ResetDayYearFormat;
        return string.Format(
            GatewayText.QuotaExhaustedUntil,
            charge.Limit,
            moment.ToString(dayFormat, CultureInfo.CurrentCulture),
            moment.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
}
