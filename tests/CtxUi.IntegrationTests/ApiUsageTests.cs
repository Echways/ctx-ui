using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CtxUi.IntegrationTests;

public class ApiUsageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private static Task<GatewayHost> StartAtAsync() =>
        GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(Start)));

    private static async Task<JsonElement> LogAsync(ApiClient api, string query = "")
    {
        using var response = await api.GetAsync("api/v1/usage/log" + query);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiClient.ReadAsync(response);
    }

    [Fact]
    public async Task Call_shows_up_in_the_log_days_and_overview()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await StartAtAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var api = await ApiClient.IssueAsync(gateway);

        using var call = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await Eventually.TrueAsync(async () => (await LogAsync(api)).GetArrayLength() == 1, "вызов попал в журнал");
        var line = (await LogAsync(api, $"?clientId={issued.Client.Id}"))[0];
        var day = (await ApiClient.ReadAsync(await api.GetAsync("api/v1/usage/days?days=1")))[0];
        var overview = await ApiClient.ReadAsync(await api.GetAsync("api/v1/overview"));

        Assert.Equal("Иванов", line.GetProperty("client").GetString());
        Assert.Equal("echo", line.GetProperty("inbound").GetString());
        Assert.Equal("tools/call", line.GetProperty("method").GetString());
        Assert.Equal("echo", line.GetProperty("tool").GetString());
        Assert.Equal("Ok", line.GetProperty("outcome").GetString());
        Assert.Equal("2026-10-12", day.GetProperty("day").GetString());
        Assert.Equal(1, day.GetProperty("toolCalls").GetInt32());
        Assert.Equal(1, overview.GetProperty("toolCalls").GetInt32());
        Assert.Equal("echo", overview.GetProperty("inbounds")[0].GetProperty("slug").GetString());
    }

    [Theory]
    [InlineData("api/v1/usage/days?days=0", "Days: нужно число от 1 до 3650")]
    [InlineData("api/v1/usage/days?days=3651", "Days: нужно число от 1 до 3650")]
    [InlineData("api/v1/usage/log?limit=0", "Limit: нужно число от 1 до 1000")]
    [InlineData("api/v1/usage/log?limit=1001", "Limit: нужно число от 1 до 1000")]
    public async Task Out_of_range_window_is_refused(string path, string expected)
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var response = await api.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expected, (await ApiClient.ReadAsync(response)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Usage_of_an_unknown_client_is_404()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        using var log = await api.GetAsync($"api/v1/usage/log?clientId={Guid.NewGuid()}");
        using var days = await api.GetAsync($"api/v1/usage/days?clientId={Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, log.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, days.StatusCode);
    }

    [Fact]
    public async Task Empty_installation_answers_with_empty_lists()
    {
        await using var gateway = await GatewayHost.StartAsync();
        using var api = await ApiClient.IssueAsync(gateway);

        Assert.Equal(0, (await LogAsync(api)).GetArrayLength());
        Assert.Equal(0, (await ApiClient.ReadAsync(await api.GetAsync("api/v1/usage/days"))).GetArrayLength());
        Assert.Equal(0, (await ApiClient.ReadAsync(await api.GetAsync("api/v1/overview"))).GetProperty("clients").GetInt32());
    }
}
