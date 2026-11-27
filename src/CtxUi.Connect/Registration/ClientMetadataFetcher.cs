using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CtxUi.Connect.Registration;

public sealed partial class ClientMetadataFetcher(IHttpClientFactory http, ILogger<ClientMetadataFetcher> logger)
{
    public const string HttpClientName = "cimd";
    public const int MaxBytes = 5 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<ClientMetadataDocument?> FetchAsync(Uri clientId, CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(client.Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, clientId);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK)
                return Refuse($"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return Refuse("response exceeds the size limit");

            var buffer = new byte[MaxBytes + 1];
            var length = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token))
            {
                int read;
                while (length < buffer.Length
                       && (read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token)) > 0)
                    length += read;
            }

            if (length > MaxBytes)
                return Refuse("response exceeds the size limit");

            var document = ClientMetadataDocument.Parse(clientId, buffer.AsSpan(0, length),
                response.Headers.CacheControl, out var rejection);
            return rejection is null ? document : Refuse(rejection);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Refuse("request timed out");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return Refuse("request or response read failed");
        }
    }

    private ClientMetadataDocument? Refuse(string reason)
    {
        LogRejection(logger, reason);
        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client metadata document rejected: {Reason}")]
    private static partial void LogRejection(ILogger logger, string reason);

    public static SocketsHttpHandler Handler(IMetadataAddressPolicy policy)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = Timeout,
            ConnectCallback = async (context, ct) =>
            {
                var allowed = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct))
                    .Where(policy.AllowsAddress).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException(
                        $"{context.DnsEndPoint.Host} does not resolve to an allowed address");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}
