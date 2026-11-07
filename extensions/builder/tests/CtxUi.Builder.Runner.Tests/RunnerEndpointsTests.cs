using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CtxUi.Builder.Contracts;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CtxUi.Builder.Runner.Tests;

public sealed class RunnerEndpointsTests
{
    private static readonly Guid Id = RunnerHarness.Id;

    private static async Task<(RunnerHarness Harness, FakeUpstream Upstream)> PublishedAsync()
    {
        var harness = await RunnerHarness.StartAsync();
        var upstream = await FakeUpstream.StartAsync("one");
        harness.Probe.Route($"{RunnerHarness.Prefix}-r1", upstream.Address);
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        return (harness, upstream);
    }

    [Fact]
    public async Task Control_api_rejects_missing_or_wrong_token()
    {
        await using var harness = await RunnerHarness.StartAsync();

        using var anonymous = new HttpClient { BaseAddress = harness.Control.BaseAddress };
        using var missing = await anonymous.GetAsync("/v1/health");
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");
        using var wrong = await anonymous.GetAsync("/v1/health");
        anonymous.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", RunnerHarness.Token + "x");
        using var longer = await anonymous.GetAsync("/v1/health");
        using var unknownPath = await anonymous.GetAsync("/v1/nothing");
        using var mutation = await anonymous.PutAsJsonAsync($"/v1/servers/{Id}", RunnerHarness.Spec());
        using var right = await harness.Control.GetAsync("/v1/health");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, longer.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownPath.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, mutation.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Data_protection_keys_stay_in_memory()
    {
        await using var harness = await RunnerHarness.StartAsync();

        var keys = harness.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
        var repository = keys.XmlRepository;
        harness.Services.GetRequiredService<IKeyManager>()
            .CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));

        Assert.IsType<InMemoryXmlRepository>(repository);
        Assert.NotNull(keys.XmlEncryptor);
        Assert.NotEmpty(repository!.GetAllElements());
    }

    [Fact]
    public async Task Health_reports_versions_and_the_egress_container()
    {
        await using var harness = await RunnerHarness.StartAsync();

        var health = await harness.Control.GetFromJsonAsync<RunnerHealth>("/v1/health");

        Assert.Equal("29.0.0", health!.DockerVersion);
        Assert.True(health.EgressReachable);
        Assert.False(string.IsNullOrEmpty(health.Version));
    }

    [Fact]
    public async Task Route_without_or_with_wrong_secret_gets_401_and_never_reaches_a_container()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;

        using var missing = await harness.CallAsync($"/s/{Id}/mcp", null);
        using var wrong = await harness.CallAsync($"/s/{Id}/mcp", "wrong");
        using var unknown = await harness.CallAsync($"/s/{Guid.NewGuid()}/mcp");
        using var other = await harness.CallAsync($"/s/{Id}/other");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Route_secret_header_is_removed_before_forwarding()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;

        Assert.Equal("one", await harness.CallActiveAsync());

        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("/mcp", seen.Path);
        Assert.DoesNotContain(RouteSecret.Header, seen.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Long_reply_reaches_the_client_as_it_is_written()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/s/{Id}/mcp?stream=1");
        request.Headers.Add(RouteSecret.Header, RunnerHarness.Secret);
        using var response = await harness.Ingress.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);

        Assert.Equal("data: first", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await reader.ReadLineAsync();
        upstream.SecondEvent.SetResult();
        Assert.Equal("data: second", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Each_port_serves_only_its_own_endpoints()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;

        using var controlOverIngress = new HttpRequestMessage(HttpMethod.Get, "/v1/health");
        controlOverIngress.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RunnerHarness.Token);
        using var refused = await harness.Ingress.SendAsync(controlOverIngress);

        using var routeOverControl = new HttpRequestMessage(HttpMethod.Post, $"/s/{Id}/mcp");
        routeOverControl.Headers.Add(RouteSecret.Header, RunnerHarness.Secret);
        using var missing = await harness.Control.SendAsync(routeOverControl);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public async Task Release_upload_above_the_limit_gets_413()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var tooBig = await harness.PostArchiveAsync(1, new byte[16 * 1024 * 1024 + 1]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        using var unknown = await harness.Control.GetAsync($"/v1/servers/{Id}/releases/1");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Chunked_release_upload_above_the_limit_gets_413()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var content = new StreamContent(new MemoryStream(new byte[16 * 1024 * 1024 + 1]));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/servers/{Id}/releases/1")
        { Content = content };
        request.Headers.TransferEncodingChunked = true;
        using var tooBig = await harness.Control.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
    }

    [Theory]
    [InlineData("/v1/servers/not-a-guid")]
    [InlineData("/v1/servers/0f8fad5b-d9cb-469f-a165-70867728950e/releases/abc")]
    [InlineData("/v1/servers/0f8fad5b-d9cb-469f-a165-70867728950e/releases/0")]
    [InlineData("/v1/servers/0f8fad5b-d9cb-469f-a165-70867728950e/releases/-1")]
    public async Task Malformed_identifiers_never_match_a_route(string path)
    {
        await using var harness = await RunnerHarness.StartAsync();

        using var response = await harness.Control.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"network":0,"limits":{"memoryMb":512,"cpus":1,"pids":256},"routeSecret":""}""")]
    [InlineData("""{"network":0,"limits":{"memoryMb":512,"cpus":1,"pids":256}}""")]
    [InlineData("""{"network":7,"limits":{"memoryMb":512,"cpus":1,"pids":256},"routeSecret":"s"}""")]
    [InlineData("""{"network":0,"limits":{"memoryMb":1,"cpus":1,"pids":256},"routeSecret":"s"}""")]
    [InlineData("""{"network":0,"limits":{"memoryMb":512,"cpus":0,"pids":256},"routeSecret":"s"}""")]
    [InlineData("""{"network":0,"routeSecret":"s"}""")]
    public async Task Unsafe_specification_is_refused_with_400(string body)
    {
        await using var harness = await RunnerHarness.StartAsync();

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await harness.Control.PutAsync($"/v1/servers/{Id}", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var state = await harness.Control.GetAsync($"/v1/servers/{Id}");
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
    }

    [Fact]
    public async Task Unknown_server_gets_404_on_build_start_and_state()
    {
        await using var harness = await RunnerHarness.StartAsync();

        using var build = await harness.PostArchiveAsync(1);
        using var start = await harness.PostStartAsync(1);
        using var state = await harness.Control.GetAsync($"/v1/servers/{Id}");
        using var status = await harness.Control.GetAsync($"/v1/servers/{Id}/releases/1");

        Assert.Equal(HttpStatusCode.NotFound, build.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, start.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(RunnerProblems.SpecRequired, (await build.Content.ReadFromJsonAsync<ProblemDetails>())!.Type);
    }

    [Fact]
    public async Task Logs_stream_as_plain_text_from_the_active_container()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;

        using var logs = await harness.Control.GetAsync($"/v1/servers/{Id}/logs?tail=50&follow=false");

        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        Assert.Equal("text/plain", logs.Content.Headers.ContentType!.MediaType);
        Assert.Equal(harness.Docker.ServerLog, await logs.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Logs_of_a_server_without_containers_get_404()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var logs = await harness.Control.GetAsync($"/v1/servers/{Id}/logs");

        Assert.Equal(HttpStatusCode.NotFound, logs.StatusCode);
    }

    [Fact]
    public async Task Docker_below_29_stops_the_runner_at_start()
    {
        var docker = new FakeDockerEngine { EngineVersion = "28.5.2" };

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => RunnerHarness.StartAsync(docker));

        Assert.Contains("29", failure.Message, StringComparison.Ordinal);
        Assert.Contains("28.5.2", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("29.0.0", true)]
    [InlineData("29.7.2", true)]
    [InlineData("30.0.0-rc.1", true)]
    [InlineData("28.99.0", false)]
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void Docker_version_check_compares_the_major_version(string version, bool accepted)
    {
        Assert.Equal(accepted, Reconciler.IsSupportedDockerVersion(version));
    }

    [Fact]
    public async Task Runner_refuses_to_start_unless_its_own_and_the_egress_containers_are_system_ones()
    {
        var notSelf = new FakeDockerEngine();
        notSelf.SystemContainers.Remove("runner-self");
        var notEgress = new FakeDockerEngine();
        notEgress.SystemContainers.Remove("egress");

        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => RunnerHarness.StartAsync(notSelf));
        await Assert.ThrowsAsync<BuilderObjectRefusedException>(() => RunnerHarness.StartAsync(notEgress));
    }

    [Fact]
    public async Task Deleting_a_server_removes_its_objects_and_routes_and_keeps_others()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;
        var neighbour = Guid.NewGuid();
        harness.Docker.AddForeignContainer("neighbour", neighbour);

        using var deleted = await harness.Control.DeleteAsync($"/v1/servers/{Id}");
        using var again = await harness.Control.DeleteAsync($"/v1/servers/{Id}");
        using var route = await harness.CallAsync($"/s/{Id}/mcp");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
        Assert.Equal("neighbour", Assert.Single(harness.Docker.Containers).Name);
        Assert.Empty(harness.Docker.Volumes);
        Assert.Empty(harness.Docker.Networks);
        Assert.Empty(harness.Docker.Attachments);
    }

    [Fact]
    public async Task Stop_and_start_act_on_the_active_container_only()
    {
        var (harness, upstream) = await PublishedAsync();
        await using var _ = harness;
        await using var __ = upstream;
        var active = harness.Docker.Find($"{RunnerHarness.Prefix}-r1")!;

        using var stopped = await harness.Control.PostAsync($"/v1/servers/{Id}/stop", null);
        Assert.False(active.Running);
        using var started = await harness.Control.PostAsync($"/v1/servers/{Id}/start", null);

        Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, started.StatusCode);
        Assert.True(active.Running);
    }

    [Fact]
    public async Task Stop_without_an_active_release_is_a_conflict()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var stopped = await harness.Control.PostAsync($"/v1/servers/{Id}/stop", null);

        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
    }
}
