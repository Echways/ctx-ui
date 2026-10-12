using Microsoft.AspNetCore.Http;
using CtxUi.Core.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace CtxUi.Gateway;

internal sealed class UpstreamTransformer(InboundRoute route, ClientAccess client) : HttpTransformer
{
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken)
    {
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        // Адрес upstream берётся из inbound'а целиком; путь и строка запроса клиента в него не попадают.
        proxyRequest.RequestUri = route.Url;
        proxyRequest.Headers.Host = null;
        _ = client;
    }
}
