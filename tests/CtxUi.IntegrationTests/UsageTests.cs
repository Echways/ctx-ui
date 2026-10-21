using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;

namespace CtxUi.IntegrationTests;

public class UsageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static Task<Client> ReadClientAsync(GatewayHost gateway) =>
        gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync("Иванов"));

    [Fact]
    public async Task Use_is_recorded_on_the_first_request_and_then_at_most_once_a_minute()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        Assert.Null((await ReadClientAsync(gateway)).LastUsedAt);

        using var first = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        await gateway.FlushUsageAsync();
        var afterFirst = await ReadClientAsync(gateway);
        clock.Advance(TimeSpan.FromSeconds(30));
        using var second = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        await gateway.FlushUsageAsync();
        var afterSecond = await ReadClientAsync(gateway);
        clock.Advance(TimeSpan.FromSeconds(31));
        using var third = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        await gateway.FlushUsageAsync();
        var afterThird = await ReadClientAsync(gateway);

        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(Start, afterFirst.FirstUsedAt);
        Assert.Equal(Start, afterFirst.LastUsedAt);
        Assert.Equal(Start, afterSecond.LastUsedAt);
        Assert.Equal(Start, afterThird.FirstUsedAt);
        Assert.Equal(Start.AddSeconds(61), afterThird.LastUsedAt);
    }

    [Fact]
    public async Task Rejected_request_is_not_a_use()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/nope", issued.Token));

        await gateway.FlushUsageAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await ReadClientAsync(gateway)).LastUsedAt);
    }

    [Fact]
    public async Task Last_use_is_saved_when_the_service_stops()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Configuration["CtxUi:FlushInterval"] = "01:00:00";
        });
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
        await gateway.RestartAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Start, (await ReadClientAsync(gateway)).LastUsedAt);
    }
}
