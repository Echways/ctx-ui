using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Runner;
using CtxUi.Builder.Servers;
using CtxUi.Builder.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class BuilderEndpointsTests
{
    private sealed class EndpointHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EndpointHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<EndpointHost> StartAsync(PublishHarness harness)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(harness.Services.GetRequiredService<ServerCatalog>());
            builder.Services.AddSingleton(harness.Services.GetRequiredService<PublishQueue>());
            builder.Services.AddTransient(_ => harness.Services.GetRequiredService<ReleaseCatalog>());
            builder.Services.AddSingleton(harness.Services.GetRequiredService<RunnerSettings>());
            var app = builder.Build();
            BuilderEndpoints.Map(app);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
                .Single();
            return new EndpointHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private static async Task<(PublishHarness Harness, EndpointHost Host, Guid Server)> StartedAsync(
        bool configured = true)
    {
        var harness = await PublishHarness.CreateAsync(configured);
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        return (harness, await EndpointHost.StartAsync(harness), server);
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task Publish_endpoint_starts_a_publish_and_returns_its_number()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;

        var response = await host.Client.PostAsync($"/servers/{server}/publish", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var number = (await JsonOf(response)).GetProperty("release").GetInt32();
        Assert.Equal(1, number);
        await harness.WaitIdleAsync(server);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, number)).Status);
    }

    [Fact]
    public async Task Publish_endpoint_can_build_without_activating()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;

        var response = await host.Client.PostAsync($"/servers/{server}/publish?mode=BuildOnly", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await harness.WaitIdleAsync(server);
        Assert.Equal(ReleaseState.Candidate, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Null((await harness.ServerAsync(server)).ActiveReleaseId);
    }

    [Fact]
    public async Task Rollback_endpoint_activates_an_older_release()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        await harness.PublishAndWaitAsync(server);
        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);

        var response = await host.Client.PostAsync($"/servers/{server}/rollback/1", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await harness.WaitIdleAsync(server);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
    }

    [Fact]
    public async Task Unknown_server_or_release_is_not_found()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/servers/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.GetAsync($"/servers/{Guid.NewGuid()}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PostAsync($"/servers/{Guid.NewGuid()}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await host.Client.PostAsync($"/servers/{server}/rollback/9", null)).StatusCode);
    }

    [Fact]
    public async Task Rolling_back_to_the_active_release_is_a_conflict()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        var number = await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);

        var response = await host.Client.PostAsync($"/servers/{server}/rollback/{number}", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Publish_while_another_is_running_is_a_conflict()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        harness.Runner.HoldBuilds = true;
        Assert.Equal(HttpStatusCode.Accepted,
            (await host.Client.PostAsync($"/servers/{server}/publish", null)).StatusCode);

        var response = await host.Client.PostAsync($"/servers/{server}/publish", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        harness.Runner.HoldBuilds = false;
    }

    [Fact]
    public async Task Invalid_publish_mode_is_a_bad_request()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;

        Assert.Equal(HttpStatusCode.BadRequest,
            (await host.Client.PostAsync($"/servers/{server}/publish?mode=sideways", null)).StatusCode);
    }

    [Fact]
    public async Task Unreachable_runner_is_a_bad_gateway()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        harness.Runner.Unreachable = true;

        var response = await host.Client.PostAsync($"/servers/{server}/publish", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(string.IsNullOrEmpty((await JsonOf(response)).GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Unconfigured_runner_is_service_unavailable()
    {
        var (harness, host, server) = await StartedAsync(false);
        await using var _ = harness;
        await using var __ = host;

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await host.Client.PostAsync($"/servers/{server}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await host.Client.PostAsync($"/servers/{server}/rollback/1", null)).StatusCode);
    }

    [Fact]
    public async Task Server_list_and_details_show_status_and_releases_without_secret_values()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        await harness.WithScopeAsync(async services =>
        {
            await services.GetRequiredService<ServerCatalog>()
                .SetSecretAsync(server, "API_KEY", "top-secret-value", default);
            return true;
        });
        var number = await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);

        var listResponse = await host.Client.GetStringAsync("/servers");
        var detailResponse = await host.Client.GetStringAsync($"/servers/{server}");

        var listed = JsonDocument.Parse(listResponse).RootElement.EnumerateArray().Single();
        Assert.Equal(server, listed.GetProperty("id").GetGuid());
        Assert.Equal("weather", listed.GetProperty("slug").GetString());
        Assert.Equal(number, listed.GetProperty("activeRelease").GetInt32());
        var details = JsonDocument.Parse(detailResponse).RootElement;
        Assert.Equal(["API_KEY"], details.GetProperty("secretNames").EnumerateArray().Select(name => name.GetString()));
        var release = details.GetProperty("releases").EnumerateArray().Single();
        Assert.Equal(number, release.GetProperty("number").GetInt32());
        Assert.Equal("Active", release.GetProperty("status").GetString());
        Assert.DoesNotContain("top-secret-value", listResponse + detailResponse);
    }

    [Fact]
    public async Task Export_contains_a_project_runnable_outside_the_panel_and_no_secrets()
    {
        var (harness, host, server) = await StartedAsync();
        await using var _ = harness;
        await using var __ = host;
        await harness.WithScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<ServerCatalog>();
            await catalog.SetSecretAsync(server, "API_KEY", "top-secret-value", default);
            await catalog.SetApiAsync(server, "https://api.example.com",
                new ApiAuthInput(ApiAuthKind.Bearer, Secret: "bearer-secret-value"), 0, default);
            var draft = await catalog.DraftAsync(server, default);
            await catalog.SaveResourceAsync(server,
                new BuilderResource("data://cities", "cities", "text/plain", "Berlin"), null, draft.Revision, default);
            return true;
        });

        var response = await host.Client.GetAsync($"/servers/{server}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("weather.tar.gz",
            response.Content.Headers.ContentDisposition!.FileNameStar ??
            response.Content.Headers.ContentDisposition.FileName);
        var entries = await ReadArchiveAsync(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(["builder.json", "fastmcp.json", "main.py", "mcp/hello.py"], entries.Keys.Order());
        using var settings = JsonDocument.Parse(entries["fastmcp.json"]);
        Assert.Equal("main.py", settings.RootElement.GetProperty("source").GetProperty("path").GetString());
        Assert.Equal("mcp", settings.RootElement.GetProperty("source").GetProperty("entrypoint").GetString());
        Assert.Contains("mcp = build_server()", entries["main.py"]);
        Assert.Contains("data://cities", entries["builder.json"]);
        Assert.DoesNotContain(entries.Values,
            content => content.Contains("top-secret-value") || content.Contains("bearer-secret-value") ||
                       content.Contains("API_KEY"));
    }

    private static async Task<Dictionary<string, string>> ReadArchiveAsync(byte[] archive)
    {
        var entries = new Dictionary<string, string>();
        await using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            using var content = new StreamReader(entry.DataStream!, Encoding.UTF8);
            entries[entry.Name] = await content.ReadToEndAsync();
        }

        return entries;
    }
}
