using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using CtxUi.Core.Metrics;

namespace CtxUi.Panel;

public static class MetricsAddress
{
    public const string Path = "metrics";

    public static string Of(MetricsFilter filter, int? days = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["days"] = days?.ToString(CultureInfo.InvariantCulture),
            ["client"] = filter.ClientId?.ToString(),
            ["inbound"] = filter.InboundId?.ToString(),
            ["tool"] = filter.Tool,
        };
        return QueryHelpers.AddQueryString(Path, query.Where(pair => pair.Value is not null));
    }
}
