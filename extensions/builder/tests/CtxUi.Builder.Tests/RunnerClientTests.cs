using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Runner;

namespace CtxUi.Builder.Tests;

public sealed class RunnerClientTests
{
    private static readonly Guid Id = Guid.Parse("4a5c1e0e-1d1f-4a6e-9f42-0a6f2f6b7a10");

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return respond(request);
        }
    }

    private static (RunnerClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://runner:8091/") };
        return (new RunnerClient(http), handler);
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(value) };

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    [Fact]
    public async Task Health_reads_the_runner_report()
    {
        var (client, handler) = Create(_ => Json(new RunnerHealth("1.2.3", "29.0", true)));

        var health = await client.HealthAsync(default);

        Assert.Equal(new RunnerHealth("1.2.3", "29.0", true), health);
        Assert.Equal((HttpMethod.Get, "/v1/health"), (handler.Requests[0].Method, handler.Requests[0].Path));
    }

    [Fact]
    public async Task Put_server_sends_the_spec()
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.NoContent));

        await client.PutServerAsync(Id, new ServerSpec(NetworkAccess.FullNetwork, new ServerLimits(1024, 2, 100), "route"), default);

        var request = handler.Requests.Single();
        Assert.Equal((HttpMethod.Put, $"/v1/servers/{Id}"), (request.Method, request.Path));
        Assert.Contains("\"routeSecret\":\"route\"", request.Body);
    }

    [Fact]
    public async Task Delete_server_calls_delete()
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.NoContent));

        await client.DeleteServerAsync(Id, default);

        Assert.Equal((HttpMethod.Delete, $"/v1/servers/{Id}"), (handler.Requests[0].Method, handler.Requests[0].Path));
    }

    [Fact]
    public async Task Server_state_is_read()
    {
        var state = new ServerState(Id, 2, [new ReleaseStatus(2, ReleaseState.Active, "log", null, "hash", null)]);
        var (client, _) = Create(_ => Json(state));

        var read = await client.ServerAsync(Id, default);

        Assert.Equal(2, read.ActiveRelease);
        Assert.Equal(ReleaseState.Active, Assert.Single(read.Releases).State);
    }

    [Fact]
    public async Task Build_uploads_the_archive_bytes()
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.Accepted));

        await client.BuildReleaseAsync(Id, 3, new MemoryStream(Encoding.UTF8.GetBytes("archive")), default);

        var request = handler.Requests.Single();
        Assert.Equal((HttpMethod.Post, $"/v1/servers/{Id}/releases/3"), (request.Method, request.Path));
        Assert.Equal("archive", request.Body);
    }

    [Fact]
    public async Task Release_status_is_read()
    {
        var (client, handler) = Create(_ => Json(new ReleaseStatus(3, ReleaseState.Built, "built", "lock", "env", null)));

        var status = await client.ReleaseAsync(Id, 3, default);

        Assert.Equal(ReleaseState.Built, status.State);
        Assert.Equal($"/v1/servers/{Id}/releases/3", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task Start_sends_the_environment()
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.Accepted));

        await client.StartReleaseAsync(Id, 3, new StartRelease(new Dictionary<string, string> { ["KEY"] = "value" }), default);

        var request = handler.Requests.Single();
        Assert.Equal((HttpMethod.Post, $"/v1/servers/{Id}/releases/3/start"), (request.Method, request.Path));
        Assert.Contains("\"KEY\":\"value\"", request.Body);
    }

    [Fact]
    public async Task Activate_returns_the_activated_release()
    {
        var (client, handler) = Create(_ => Json(new ReleaseStatus(3, ReleaseState.Active, "", null, null, null)));

        var status = await client.ActivateReleaseAsync(Id, 3, default);

        Assert.Equal(ReleaseState.Active, status.State);
        Assert.Equal((HttpMethod.Post, $"/v1/servers/{Id}/releases/3/activate"), (handler.Requests[0].Method, handler.Requests[0].Path));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("start")]
    public async Task Stop_and_start_post_to_the_server(string action)
    {
        var (client, handler) = Create(_ => Status(HttpStatusCode.NoContent));

        await (action == "stop" ? client.StopServerAsync(Id, default) : client.StartServerAsync(Id, default));

        Assert.Equal((HttpMethod.Post, $"/v1/servers/{Id}/{action}"), (handler.Requests[0].Method, handler.Requests[0].Path));
    }

    [Fact]
    public async Task Logs_stream_as_text_with_tail_and_follow()
    {
        var (client, handler) = Create(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("line one\nline two\n") });

        var text = new StringBuilder();
        await foreach (var chunk in client.LogsAsync(Id, 50, true, default))
            text.Append(chunk);

        Assert.Equal("line one\nline two\n", text.ToString());
        Assert.Equal($"/v1/servers/{Id}/logs?tail=50&follow=true", handler.Requests.Single().Path);
    }

    [Fact]
    public async Task Failure_carries_status_and_problem_detail()
    {
        var (client, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"status\":409,\"detail\":\"A build is already running.\"}", Encoding.UTF8, new MediaTypeHeaderValue("application/problem+json"))
        });

        var failure = await Assert.ThrowsAsync<RunnerException>(() => client.BuildReleaseAsync(Id, 1, new MemoryStream(), default));

        Assert.Equal(HttpStatusCode.Conflict, failure.Status);
        Assert.Equal("A build is already running.", failure.Message);
    }

    [Fact]
    public async Task Failure_without_detail_still_names_the_status()
    {
        var (client, _) = Create(_ => Status(HttpStatusCode.Unauthorized));

        var failure = await Assert.ThrowsAsync<RunnerException>(() => client.HealthAsync(default));

        Assert.Equal(HttpStatusCode.Unauthorized, failure.Status);
        Assert.Contains("401", failure.Message);
    }

    [Fact]
    public async Task Unreachable_runner_becomes_a_runner_exception()
    {
        var (client, _) = Create(_ => throw new HttpRequestException("connection refused"));

        var failure = await Assert.ThrowsAsync<RunnerException>(() => client.HealthAsync(default));

        Assert.Null(failure.Status);
    }
}
