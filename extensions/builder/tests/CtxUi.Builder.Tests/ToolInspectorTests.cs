using System.ComponentModel;
using System.Text.Json.Nodes;
using CtxUi.Builder.Publishing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CtxUi.Builder.Tests;

[McpServerToolType]
public sealed class InspectorTools
{
    public const string SquidPage = "<title>ERROR: Access Denied</title> ERR_ACCESS_DENIED The requested URL could not be retrieved";

    [McpServerTool(Name = "echo"), Description("Repeats the text.")]
    public static string Echo(string text) => $"echo {text}";

    [McpServerTool(Name = "forbidden")]
    public static CallToolResult Forbidden() => new() { IsError = true, Content = [new TextContentBlock { Text = $"HTTP error 403: {SquidPage}" }] };

    [McpServerTool(Name = "broken")]
    public static CallToolResult Broken() => new() { IsError = true, Content = [new TextContentBlock { Text = "HTTP error 403: Forbidden by the upstream API" }] };
}

public sealed class ToolInspectorTests
{
    private sealed class IngressHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private IngressHost(WebApplication app, string address, List<string> paths)
        {
            _app = app;
            Address = address;
            Paths = paths;
        }

        public string Address { get; }

        public List<string> Paths { get; }

        public static async Task<IngressHost> StartAsync(string secret)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<InspectorTools>();
            var app = builder.Build();
            var paths = new List<string>();
            app.Use(async (context, next) =>
            {
                paths.Add(context.Request.Path.Value!);
                if (context.Request.Headers[ToolInspector.SecretHeader] != secret)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next();
            });
            app.MapMcp("/s/{id:guid}/mcp");
            app.MapMcp("/s/{id:guid}/r/{release:int}/mcp");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new IngressHost(app, address, paths);
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    [Fact]
    public async Task Tools_are_listed_through_the_route_with_the_secret_header()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await using var ingress = await IngressHost.StartAsync(await harness.RouteSecretAsync(server));

        var tools = await harness.CreateInspector(ingress.Address).ListAsync(server, null, default);

        Assert.Contains(tools, tool => tool.Name == "echo" && tool.Description == "Repeats the text." && tool.InputSchema.GetProperty("properties").TryGetProperty("text", out _));
        Assert.All(ingress.Paths, path => Assert.Equal($"/s/{server}/mcp", path));
    }

    [Fact]
    public async Task A_candidate_is_reached_through_its_release_route()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await using var ingress = await IngressHost.StartAsync(await harness.RouteSecretAsync(server));

        await harness.CreateInspector(ingress.Address).ListAsync(server, 3, default);

        Assert.All(ingress.Paths, path => Assert.Equal($"/s/{server}/r/3/mcp", path));
    }

    [Fact]
    public async Task A_wrong_route_secret_is_refused()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await using var ingress = await IngressHost.StartAsync("another-secret");

        await Assert.ThrowsAnyAsync<Exception>(() => harness.CreateInspector(ingress.Address).ListAsync(server, null, default));
    }

    [Fact]
    public async Task A_call_returns_the_text_and_the_elapsed_time()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await using var ingress = await IngressHost.StartAsync(await harness.RouteSecretAsync(server));

        var outcome = await harness.CreateInspector(ingress.Address).CallAsync(server, null, "echo", new JsonObject { ["text"] = "hi" }, default);

        Assert.False(outcome.IsError);
        Assert.Equal("echo hi", outcome.Text);
        Assert.False(outcome.ProxyRefused);
        Assert.True(outcome.Elapsed > TimeSpan.Zero);
    }

    [Fact]
    public async Task Forbidden_by_proxy_is_recognized_in_a_tool_result()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await using var ingress = await IngressHost.StartAsync(await harness.RouteSecretAsync(server));
        var inspector = harness.CreateInspector(ingress.Address);

        var refused = await inspector.CallAsync(server, null, "forbidden", [], default);
        var upstream = await inspector.CallAsync(server, null, "broken", [], default);

        Assert.True(refused.IsError);
        Assert.True(refused.ProxyRefused);
        Assert.Contains("ERR_ACCESS_DENIED", refused.Text);
        Assert.True(upstream.IsError);
        Assert.False(upstream.ProxyRefused);
    }

    [Theory]
    [InlineData("ProxyError: 403 Forbidden from proxy squid:3128", true)]
    [InlineData("httpx.ProxyError: proxy answered 403", true)]
    [InlineData("X-Squid-Error: ERR_ACCESS_DENIED 0", true)]
    [InlineData("HTTP error 403: Forbidden", false)]
    [InlineData("Connection refused", false)]
    public void Squid_refusals_are_matched_by_their_text(string text, bool expected) =>
        Assert.Equal(expected, ToolInspector.IsProxyRefusal(text));

    [Fact]
    public async Task Without_runner_settings_the_inspector_says_what_is_missing()
    {
        var inspector = new ToolInspector(new CtxUi.Builder.Runner.RunnerSettings(null, null, null), null!, null!);

        var refused = await Assert.ThrowsAsync<Enfilade.PanelException>(() => inspector.ListAsync(Guid.NewGuid(), null, default));

        Assert.Equal(string.Format(BuilderText.RunnerNotConfigured, "RunnerUrl, RunnerIngressUrl, RunnerToken"), refused.Message);
    }
}
