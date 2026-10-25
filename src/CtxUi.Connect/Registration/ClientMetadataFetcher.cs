using System.Net;
using System.Net.Sockets;

namespace CtxUi.Connect.Registration;

public sealed class ClientMetadataFetcher(IHttpClientFactory http)
{
    public const string HttpClientName = "cimd";
    public const int MaxBytes = 5 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<ClientMetadataDocument?> FetchAsync(Uri clientId, CancellationToken ct)
    {
        try
        {
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, clientId);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBytes)
                return null;

            var buffer = new byte[MaxBytes + 1];
            var length = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                int read;
                while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), ct)) > 0)
                    length += read;
            }

            return length > MaxBytes ? null : ClientMetadataDocument.Parse(clientId, buffer.AsSpan(0, length), response.Headers.CacheControl);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
                                              || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static SocketsHttpHandler Handler(IMetadataAddressPolicy policy) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = Timeout,
        ConnectCallback = async (context, ct) =>
        {
            var allowed = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct)).Where(policy.AllowsAddress).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to an allowed address");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}
