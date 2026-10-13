using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;

namespace CtxUi.IntegrationTests;

public class QuotaTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private static string ToolCall(string id = "1", string tool = "echo") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

    private static string Request(string method, string id = "1") =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"}""";

    private static async Task<HttpResponseMessage> CallAsync(GatewayHost gateway, string token, string body, string inbound = "echo") =>
        await gateway.Http.SendAsync(GatewayHost.Post($"/mcp/{inbound}", token, body));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<bool> IsQuotaRefusalAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK &&
        (await response.Content.ReadAsStringAsync()).Contains("Лимит вызовов исчерпан");

    [Fact]
    public async Task Call_over_the_quota_gets_a_tool_error_the_model_can_read_and_stays_away_from_the_upstream()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 2, ResetEveryDays: 30));

        using var first = await CallAsync(gateway, issued.Token, ToolCall("1"));
        using var second = await CallAsync(gateway, issued.Token, ToolCall("2"));
        using var third = await CallAsync(gateway, issued.Token, ToolCall("3"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal("application/json", third.Content.Headers.ContentType?.MediaType);
        var answer = await JsonAsync(third);
        Assert.Equal("2.0", answer.GetProperty("jsonrpc").GetString());
        Assert.Equal(3, answer.GetProperty("id").GetInt32());
        Assert.True(answer.GetProperty("result").GetProperty("isError").GetBoolean());
        var content = Assert.Single(answer.GetProperty("result").GetProperty("content").EnumerateArray().ToList());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal(
            "Лимит вызовов исчерпан: 2 за период. Обновится 11 ноября в 10:00 UTC.",
            content.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Client_out_of_quota_can_still_connect_and_ask_what_tools_exist()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());
        using var refused = await CallAsync(gateway, issued.Token, ToolCall());

        using var initialize = await CallAsync(gateway, issued.Token, Request("initialize"));
        using var initialized = await CallAsync(gateway, issued.Token, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        using var list = await CallAsync(gateway, issued.Token, Request("tools/list"));
        using var listen = new HttpRequestMessage(HttpMethod.Get, "/mcp/echo");
        listen.Headers.Authorization = new("Bearer", issued.Token);
        using var stream = await gateway.Http.SendAsync(listen);

        Assert.True(await IsQuotaRefusalAsync(refused));
        Assert.All([initialize, initialized, list, stream], response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(5, upstream.Requests.Count);
    }

    [Fact]
    public async Task Only_tool_calls_use_up_the_quota()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));

        foreach (var method in new[] { "initialize", "ping", "tools/list", "resources/read", "prompts/get", "tools/call/extra" })
        {
            using var free = await CallAsync(gateway, issued.Token, Request(method));
            Assert.False(await IsQuotaRefusalAsync(free));
        }

        using var call = await CallAsync(gateway, issued.Token, ToolCall());
        using var over = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.False(await IsQuotaRefusalAsync(call));
        Assert.True(await IsQuotaRefusalAsync(over));
    }

    [Fact]
    public async Task Quota_comes_back_when_the_reset_interval_passes()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1, ResetEveryDays: 7));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());

        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        using var dayBefore = await CallAsync(gateway, issued.Token, ToolCall());
        clock.Advance(TimeSpan.FromSeconds(1));
        using var afterReset = await CallAsync(gateway, issued.Token, ToolCall());
        using var overAgain = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.True(await IsQuotaRefusalAsync(dayBefore));
        Assert.False(await IsQuotaRefusalAsync(afterReset));
        Assert.Contains("Обновится 26 октября в 10:00 UTC.", await overAgain.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(401)]
    [InlineData(404)]
    public async Task Call_the_upstream_did_not_serve_is_given_back(int upstreamStatus)
    {
        await using var upstream = await FakeUpstream.StartAsync((app, recorder) => app.Map("/broken", async context =>
        {
            await recorder.RecordAsync(context);
            context.Response.StatusCode = upstreamStatus;
        }));
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("broken", upstream.BaseUrl + "/broken");
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["broken", "echo"], limits: new ClientLimits(QuotaCalls: 1));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await CallAsync(gateway, issued.Token, ToolCall(), "broken");
            Assert.Equal(upstreamStatus, (int)failed.StatusCode);
        }

        using var served = await CallAsync(gateway, issued.Token, ToolCall());
        using var over = await CallAsync(gateway, issued.Token, ToolCall(), "broken");

        Assert.False(await IsQuotaRefusalAsync(served));
        Assert.True(await IsQuotaRefusalAsync(over));
        Assert.Equal(4, upstream.Requests.Count);
    }

    [Fact]
    public async Task Call_to_an_unreachable_upstream_is_given_back()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("dead", "http://127.0.0.1:1/mcp");
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["dead", "echo"], limits: new ClientLimits(QuotaCalls: 1));

        using var first = await CallAsync(gateway, issued.Token, ToolCall(), "dead");
        using var second = await CallAsync(gateway, issued.Token, ToolCall(), "dead");
        using var served = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.Equal(HttpStatusCode.BadGateway, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, second.StatusCode);
        Assert.False(await IsQuotaRefusalAsync(served));
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData("\"abc-7\"")]
    [InlineData("\"ключ \\\"7\\\"\"")]
    [InlineData("9007199254740993")]
    [InlineData("1.50")]
    public async Task Refusal_carries_the_request_id_exactly_as_it_was_sent(string id)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());

        using var refused = await CallAsync(gateway, issued.Token, ToolCall(id));

        Assert.Equal(id, (await JsonAsync(refused)).GetProperty("id").GetRawText());
    }

    [Fact]
    public async Task Tool_call_without_an_id_over_the_quota_is_dropped_with_202()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());

        using var refused = await CallAsync(
            gateway, issued.Token, """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo"}}""");

        Assert.Equal(HttpStatusCode.Accepted, refused.StatusCode);
        Assert.Equal("", await refused.Content.ReadAsStringAsync());
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task Batch_is_charged_per_call_and_refused_whole_when_it_does_not_fit()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 3));

        using var fits = await CallAsync(
            gateway, issued.Token, $"[{ToolCall("1")},{ToolCall("2")},{Request("ping", "3")}]");
        using var tooMany = await CallAsync(
            gateway,
            issued.Token,
            $$"""[{{ToolCall("4")}},{{Request("ping", "5")}},{"jsonrpc":"2.0","method":"notifications/progress"},{{ToolCall("\"six\"")}}]""");
        using var lastOne = await CallAsync(gateway, issued.Token, ToolCall("7"));
        using var over = await CallAsync(gateway, issued.Token, ToolCall("8"));

        Assert.False(await IsQuotaRefusalAsync(fits));
        Assert.Equal(HttpStatusCode.OK, tooMany.StatusCode);
        var answers = (await JsonAsync(tooMany)).EnumerateArray().ToList();
        Assert.Equal(["4", "5", "\"six\""], answers.Select(answer => answer.GetProperty("id").GetRawText()));
        Assert.True(answers[0].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Equal(-32000, answers[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("Лимит вызовов исчерпан", answers[1].GetProperty("error").GetProperty("message").GetString());
        Assert.True(answers[2].GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.False(await IsQuotaRefusalAsync(lastOne));
        Assert.True(await IsQuotaRefusalAsync(over));
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"ping","method":"tools/call","params":{"name":"echo"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call"} {"jsonrpc":"2.0","id":2,"method":"tools/call"}""")]
    public async Task Body_the_gateway_cannot_read_unambiguously_gets_400_and_never_reaches_the_upstream(string body)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));

        using var response = await CallAsync(gateway, issued.Token, body);
        using var call = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var answer = await JsonAsync(response);
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("id").ValueKind);
        Assert.Equal(-32600, answer.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("шлюз не разобрал тело запроса", answer.GetProperty("error").GetProperty("message").GetString());
        Assert.False(await IsQuotaRefusalAsync(call));
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_over_the_inbound_limit_gets_413(bool chunked)
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        var inbound = await gateway.AddInboundAsync("echo", upstream.McpUrl);
        await gateway.ArrangeAsync(async services =>
        {
            var db = services.GetRequiredService<CtxUiDbContext>();
            (await db.Inbounds.FindAsync(inbound.Id))!.MaxBodyBytes = 1024;
            return await db.SaveChangesAsync();
        });
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var padding = new string('x', 2048);
        var bytes = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"pad\":\"" + padding + "\"}}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/echo")
        {
            Content = chunked ? new StreamContent(new MemoryStream(bytes)) : new ByteArrayContent(bytes),
        };
        request.Headers.Authorization = new("Bearer", issued.Token);
        request.Headers.TransferEncodingChunked = chunked;
        using var tooLarge = await gateway.Http.SendAsync(request);
        using var small = await CallAsync(gateway, issued.Token, Request("ping"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Contains("\"error\":\"body_too_large\"", await tooLarge.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Fact]
    public async Task Body_reaches_the_upstream_byte_for_byte_after_the_gateway_has_read_it()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 5));
        var body = "{ \"jsonrpc\" : \"2.0\",\n\t\"id\":1, \"method\":\"tools\\/call\", \"params\":{\"name\":\"эхо\",\"arguments\":{\"text\":\"привет \\u043c\\u0438\\u0440\"}} }";

        using var response = await CallAsync(gateway, issued.Token, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, Assert.Single(upstream.Requests).Body);
    }

    [Fact]
    public async Task Counters_survive_a_restart()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 2, ResetEveryDays: 30));
        using var beforeRestart = await CallAsync(gateway, issued.Token, ToolCall());

        await gateway.RestartAsync();
        using var afterRestart = await CallAsync(gateway, issued.Token, ToolCall());
        using var over = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.False(await IsQuotaRefusalAsync(beforeRestart));
        Assert.False(await IsQuotaRefusalAsync(afterRestart));
        Assert.True(await IsQuotaRefusalAsync(over));
    }

    [Fact]
    public async Task Used_calls_are_saved_where_the_administrator_can_see_them()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var first = await CallAsync(gateway, issued.Token, ToolCall());
        using var second = await CallAsync(gateway, issued.Token, ToolCall());

        await Eventually.TrueAsync(
            async () =>
            {
                var client = await gateway.ChangeAsync(services => services.GetRequiredService<ClientService>().GetAsync("Иванов"));
                return QuotaPeriod.UsedCalls(client, DateTimeOffset.UtcNow) == 2;
            },
            "счётчик вызовов записан в базу");
    }

    [Fact]
    public async Task Reset_made_by_another_process_gives_the_quota_back()
    {
        var clock = new FakeTimeProvider(Start);
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Services.AddSingleton<TimeProvider>(clock));
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());
        using var refused = await CallAsync(gateway, issued.Token, ToolCall());
        clock.Advance(TimeSpan.FromHours(1));

        await gateway.ChangeAsync(async services =>
        {
            await services.GetRequiredService<ClientService>().ResetQuotaAsync("Иванов");
            return true;
        });

        Assert.True(await IsQuotaRefusalAsync(refused));
        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await CallAsync(gateway, issued.Token, ToolCall());
                return !await IsQuotaRefusalAsync(response);
            },
            "после сброса вызовы снова проходят");
    }

    [Fact]
    public async Task Bigger_quota_keeps_the_calls_already_made()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await CallAsync(gateway, issued.Token, ToolCall());
        using var refused = await CallAsync(gateway, issued.Token, ToolCall());

        await gateway.SetLimitsAsync("Иванов", new ClientLimits(QuotaCalls: 2));
        using var extra = await CallAsync(gateway, issued.Token, ToolCall());
        using var over = await CallAsync(gateway, issued.Token, ToolCall());

        Assert.True(await IsQuotaRefusalAsync(refused));
        Assert.False(await IsQuotaRefusalAsync(extra));
        Assert.True(await IsQuotaRefusalAsync(over));
    }
}
