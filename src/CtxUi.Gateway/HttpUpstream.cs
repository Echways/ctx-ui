using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Routing;
using Yarp.ReverseProxy.Forwarder;

namespace CtxUi.Gateway;

public enum ForwardOutcome
{
    Completed,
    ClientAborted,
    UpstreamFailed,
}

public readonly record struct Forwarded(ForwardOutcome Outcome, long ResponseBytes);

public sealed partial class HttpUpstream(IHttpForwarder forwarder, ILogger<HttpUpstream> logger) : IDisposable
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

    public async Task<Forwarded> ForwardAsync(HttpContext context, InboundRoute route, ClientAccess client)
    {
        var traffic = new ResponseTraffic();
        context.Features.Set(traffic);
        return new Forwarded(await SendAsync(context, route, client), traffic.Bytes);
    }

    public void Dispose() => _client.Dispose();

    private async Task<ForwardOutcome> SendAsync(HttpContext context, InboundRoute route, ClientAccess client)
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
                return ForwardOutcome.Completed;
            case ForwarderError.RequestCanceled
                or ForwarderError.RequestBodyCanceled
                or ForwarderError.RequestBodyClient
                or ForwarderError.ResponseBodyCanceled
                or ForwarderError.ResponseBodyClient:
                ClientAborted(logger, route.Slug, error);
                return ForwardOutcome.ClientAborted;
            default:
                UpstreamFailed(logger, route.Slug, error, context.Features.Get<IForwarderErrorFeature>()?.Exception?.Message);
                return ForwardOutcome.UpstreamFailed;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "inbound {Slug}: the client aborted the request ({Error})")]
    private static partial void ClientAborted(ILogger logger, string slug, ForwarderError error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "inbound {Slug}: the upstream did not answer ({Error}): {Reason}")]
    private static partial void UpstreamFailed(ILogger logger, string slug, ForwarderError error, string? reason);
}
