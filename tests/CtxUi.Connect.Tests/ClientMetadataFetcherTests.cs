using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using CtxUi.Connect.Registration;

namespace CtxUi.Connect.Tests;

public class ClientMetadataFetcherTests
{
    private static readonly Uri ClientId = new("https://client.example/client.json");

    [Fact]
    public async Task Deadline_covers_the_response_body()
    {
        using var body = new WaitingBody();
        var fetcher = Fetcher(body);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Assert.Null(await fetcher.FetchAsync(ClientId, guard.Token));
        Assert.True(body.ReadStarted.Task.IsCompleted);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_response_body_propagates()
    {
        using var body = new WaitingBody();
        var fetcher = Fetcher(body);
        using var caller = new CancellationTokenSource();
        var fetch = fetcher.FetchAsync(ClientId, caller.Token);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Theory]
    [InlineData(ClientMetadataFetcher.MaxBytes, true)]
    [InlineData(ClientMetadataFetcher.MaxBytes + 1, false)]
    public async Task Body_without_content_length_is_bounded(int length, bool accepted)
    {
        var document = $$"""
            {"client_id":"{{ClientId.OriginalString}}","client_name":"Test","redirect_uris":["https://client.example/cb"]}
            """;
        using var body = new UnseekableBody(Encoding.UTF8.GetBytes(document.PadRight(length)));

        var result = await Fetcher(body).FetchAsync(ClientId, default);

        Assert.Equal(accepted, result is not null);
    }

    private static ClientMetadataFetcher Fetcher(Stream body)
    {
        return new ClientMetadataFetcher(new ClientFactory(body), NullLogger<ClientMetadataFetcher>.Instance);
    }

    private sealed class ClientFactory(Stream body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new ResponseHandler(body)) { Timeout = TimeSpan.FromMilliseconds(200) };
        }
    }

    private sealed class ResponseHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
        }
    }

    private sealed class UnseekableBody(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingBody : MemoryStream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
