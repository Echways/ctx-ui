using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using CtxUi.Core.Clients;

namespace CtxUi.IntegrationTests;

[McpServerToolType]
public sealed class EchoTools
{
    [McpServerTool(Name = "echo"), Description("Возвращает переданный текст")]
    public static string Echo(string text) => "echo: " + text;
}

public class McpEndToEndTests
{
    internal static async Task<WebApplication> StartMcpServerAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<EchoTools>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    private static async Task<string> CallEchoAsync(string endpoint, string? bearer, string? protocolVersion)
    {
        var options = bearer is null
            ? new HttpClientTransportOptions { Endpoint = new Uri(endpoint) }
            : new HttpClientTransportOptions
            {
                Endpoint = new Uri(endpoint),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + bearer },
            };
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(options),
            protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion });

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "echo");
        var result = await client.CallToolAsync("echo", new Dictionary<string, object?> { ["text"] = "привет" });
        return Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    public async Task Tool_call_works_through_the_gateway_on_every_protocol_revision(string? protocolVersion)
    {
        await using var server = await StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        var answer = await CallEchoAsync(gateway.BaseUrl + "/mcp/echo", issued.Token, protocolVersion);

        Assert.Equal("echo: привет", answer);
    }

    [Fact]
    public async Task Tool_call_works_with_the_token_in_the_address()
    {
        await using var server = await StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        var answer = await CallEchoAsync($"{gateway.BaseUrl}/k/{issued.Token}/mcp/echo", bearer: null, protocolVersion: null);

        Assert.Equal("echo: привет", answer);
    }

    [Fact]
    public async Task Disabled_client_cannot_connect()
    {
        await using var server = await StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ArrangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().SetEnabledAsync(issued.Client.Id, false);
            return true;
        });

        await Assert.ThrowsAnyAsync<Exception>(() => CallEchoAsync(gateway.BaseUrl + "/mcp/echo", issued.Token, null));
    }
}
