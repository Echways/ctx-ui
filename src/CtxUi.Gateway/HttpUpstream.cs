using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace CtxUi.Gateway;

/// <summary>
/// Прозрачно пересылает запрос в удалённый MCP-сервер. Тело и ответ идут потоком без разбора,
/// поэтому работают любая ревизия протокола и ответы SSE.
/// </summary>
public sealed class HttpUpstream(IHttpForwarder forwarder, ILogger<HttpUpstream> logger) : IDisposable
{
    private readonly HttpMessageInvoker _client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    public async Task ForwardAsync(HttpContext context, InboundRoute route, ClientAccess client)
    {
        var config = new ForwarderRequestConfig { ActivityTimeout = route.RequestTimeout };
        var error = await forwarder.SendAsync(
            context,
            route.Url.GetLeftPart(UriPartial.Authority),
            _client,
            config,
            new UpstreamTransformer(route, client));

        switch (error)
        {
            case ForwarderError.None:
                return;
            case ForwarderError.RequestCanceled or ForwarderError.RequestBodyCanceled or ForwarderError.ResponseBodyCanceled:
                logger.LogInformation("inbound {Slug}: клиент оборвал запрос ({Error})", route.Slug, error);
                return;
            default:
                logger.LogWarning(
                    "inbound {Slug}: upstream не ответил ({Error}): {Reason}",
                    route.Slug,
                    error,
                    context.Features.Get<IForwarderErrorFeature>()?.Exception?.Message);
                return;
        }
    }

    public void Dispose() => _client.Dispose();
}
