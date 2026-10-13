using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using CtxUi.Core.Clients;

namespace CtxUi.IntegrationTests;

public class McpLimitsEndToEndTests
{
    private static readonly Dictionary<string, object?> Arguments = new() { ["text"] = "привет" };

    private static Task<McpClient> ConnectAsync(GatewayHost gateway, string token, string? protocolVersion) =>
        McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(gateway.BaseUrl + "/mcp/echo"),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }),
            protocolVersion is null ? null : new McpClientOptions { ProtocolVersion = protocolVersion });

    private static string Text(CallToolResult result) => Assert.Single(result.Content.OfType<TextContentBlock>()).Text;

    [Theory]
    [InlineData(null)]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    public async Task Call_over_the_quota_reaches_a_real_client_as_a_tool_error_on_every_protocol_revision(string? protocolVersion)
    {
        await using var server = await McpEndToEndTests.StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1, ResetEveryDays: 30));
        await using var client = await ConnectAsync(gateway, issued.Token, protocolVersion);

        var served = await client.CallToolAsync("echo", Arguments);
        var refused = await client.CallToolAsync("echo", Arguments);
        var tools = await client.ListToolsAsync();

        Assert.NotEqual(true, served.IsError);
        Assert.Equal("echo: привет", Text(served));
        Assert.True(refused.IsError);
        Assert.StartsWith("Лимит вызовов исчерпан: 1 за период. Обновится ", Text(refused));
        Assert.Contains(tools, tool => tool.Name == "echo");
    }

    [Fact]
    public async Task Client_that_ran_out_of_quota_can_connect_again_and_gets_the_same_refusal()
    {
        await using var server = await McpEndToEndTests.StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        await using (var first = await ConnectAsync(gateway, issued.Token, null))
            await first.CallToolAsync("echo", Arguments);

        await using var second = await ConnectAsync(gateway, issued.Token, null);
        var refused = await second.CallToolAsync("echo", Arguments);

        Assert.True(refused.IsError);
        Assert.Equal("Лимит вызовов исчерпан: 1. Новых вызовов не будет, обратитесь к тому, кто выдал доступ.", Text(refused));
    }

    [Fact]
    public async Task Limits_generous_enough_for_a_session_do_not_get_in_the_way()
    {
        await using var server = await McpEndToEndTests.StartMcpServerAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", server.Urls.First() + "/mcp");
        var issued = await gateway.AddClientAsync(
            "Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 600, MaxConcurrency: 1, QuotaCalls: 10, ResetEveryDays: 1));
        await using var client = await ConnectAsync(gateway, issued.Token, null);

        for (var call = 0; call < 10; call++)
            Assert.Equal("echo: привет", Text(await client.CallToolAsync("echo", Arguments)));
    }
}
