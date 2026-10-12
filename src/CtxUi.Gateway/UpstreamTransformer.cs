using Microsoft.AspNetCore.Http;
using CtxUi.Core.Inbounds;
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

        proxyRequest.RequestUri = route.Url;
        proxyRequest.Headers.Host = null;

        proxyRequest.Headers.Authorization = null;
        foreach (var name in proxyRequest.Headers.Select(header => header.Key).Where(UpstreamHeaders.IsIdentity).ToList())
            proxyRequest.Headers.Remove(name);

        foreach (var (name, value) in route.UpstreamHeaders)
        {
            proxyRequest.Headers.Remove(name);
            proxyRequest.Headers.TryAddWithoutValidation(name, value);
        }

        if (!route.ForwardIdentity)
            return;

        proxyRequest.Headers.TryAddWithoutValidation(UpstreamHeaders.ClientPrefix + "Id", client.Id.ToString());
        proxyRequest.Headers.TryAddWithoutValidation(UpstreamHeaders.ClientPrefix + "Name", Uri.EscapeDataString(client.Name));
        foreach (var (key, value) in client.Metadata)
            proxyRequest.Headers.TryAddWithoutValidation(UpstreamHeaders.MetaPrefix + key, Uri.EscapeDataString(value));
    }
}
