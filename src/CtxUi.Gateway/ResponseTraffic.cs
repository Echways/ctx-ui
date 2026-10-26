using Microsoft.AspNetCore.Http;
using Yarp.Telemetry.Consumption;

namespace CtxUi.Gateway;

public sealed class ResponseTraffic
{
    public long Bytes { get; set; }
}

public sealed class ResponseTrafficTelemetry(IHttpContextAccessor accessor) : IForwarderTelemetryConsumer
{
    public void OnContentTransferred(
        DateTime timestamp, bool isRequest, long contentLength, long iops, TimeSpan readTime, TimeSpan writeTime, TimeSpan firstReadTime)
    {
        if (!isRequest && accessor.HttpContext?.Features.Get<ResponseTraffic>() is { } traffic)
            traffic.Bytes = contentLength;
    }
}
