using Microsoft.Data.Sqlite;
using CtxUi.Core.Clients;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed class CliLimitsTests : IDisposable
{
    private const string ToolCall = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{}}}""";

    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => CliRunner.RunAsync(_dataDir, args);

    private async Task IssueAsync(string name, params string[] extra)
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");
        var added = await RunAsync(["client", "add", name, "--inbound", "echo", .. extra]);
        Assert.Equal("", added.Error);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Usage_is_a_cli_command() => Assert.True(Cli.Handles(["usage", "days"]));

    [Fact]
    public async Task Limits_given_at_issue_are_printed_with_the_connection()
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");

        var added = await RunAsync(
            "client", "add", "Иванов", "--inbound", "echo", "--rate", "60", "--concurrency", "2", "--quota", "100", "--reset-days", "30");

        Assert.Equal(0, added.Code);
        Assert.Contains("Лимиты:  запросов в минуту: 60, одновременно: 2", added.Output);
        Assert.Contains("Квота:   0 из 100 вызовов, обновится ", added.Output);
        Assert.Contains("(UTC)", added.Output);
    }

    [Fact]
    public async Task Client_without_limits_is_shown_as_unrestricted()
    {
        await IssueAsync("Иванов");

        var shown = await RunAsync("client", "show", "Иванов");
        var listed = await RunAsync("client", "list");

        Assert.Contains("Лимиты:  нет", shown.Output);
        Assert.Contains("Квота:   нет, вызовов за период: 0", shown.Output);
        Assert.Contains("0/∞", listed.Output);
        Assert.Contains("активен", listed.Output);
    }

    [Fact]
    public async Task Set_changes_only_the_limits_it_names_and_none_removes_one()
    {
        await IssueAsync("Иванов", "--rate", "60", "--concurrency", "2", "--quota", "100");

        var changed = await RunAsync("client", "set", "Иванов", "--quota", "500", "--rate", "none", "--reset-days", "7");
        var shown = await RunAsync("client", "show", "Иванов");

        Assert.Equal(0, changed.Code);
        Assert.Contains("Лимиты:  одновременно: 2", shown.Output);
        Assert.DoesNotContain("запросов в минуту", shown.Output);
        Assert.Contains("Квота:   0 из 500 вызовов, обновится ", shown.Output);
    }

    [Fact]
    public async Task Set_that_names_no_limit_leaves_them_alone()
    {
        await IssueAsync("Иванов", "--rate", "60", "--quota", "100");

        await RunAsync("client", "set", "Иванов", "--expires", "2099-01-01");
        var shown = await RunAsync("client", "show", "Иванов");

        Assert.Contains("запросов в минуту: 60", shown.Output);
        Assert.Contains("0 из 100 вызовов", shown.Output);
    }

    [Theory]
    [InlineData("client|add|Петров|--inbound|echo|--quota|0")]
    [InlineData("client|add|Петров|--inbound|echo|--rate|много")]
    [InlineData("client|add|Петров|--inbound|echo|--reset-days|-1")]
    [InlineData("client|add|Петров|--inbound|echo|--concurrency|5000")]
    [InlineData("client|add|Петров|--inbound|echo|--quota|99999999999999999999")]
    [InlineData("client|set|Иванов|--quota|0")]
    [InlineData("client|set|Иванов|--rate|1.5")]
    [InlineData("client|reset|Сидоров")]
    [InlineData("usage|days|--days|0")]
    [InlineData("usage|days|--client|Сидоров")]
    [InlineData("usage|log|--limit|100000")]
    public async Task Rule_violation_is_explained_and_exits_with_one(string commandLine)
    {
        await IssueAsync("Иванов");

        var result = await RunAsync(commandLine.Split('|'));
        var listed = await RunAsync("client", "list");

        Assert.Equal(1, result.Code);
        Assert.StartsWith("Ошибка: ", result.Error);
        Assert.Equal("", result.Output);
        Assert.DoesNotContain("Петров", listed.Output);
    }

    [Fact]
    public async Task Empty_usage_is_said_in_words()
    {
        await IssueAsync("Иванов");

        var days = await RunAsync("usage", "days");
        var log = await RunAsync("usage", "log", "--client", "Иванов");

        Assert.Equal("За это время обращений не было", days.Output.Trim());
        Assert.Equal("Журнал пуст", log.Output.Trim());
    }

    [Fact]
    public async Task Calls_made_through_the_running_server_show_up_in_list_show_and_usage()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов 4419", ["echo"], limits: new ClientLimits(QuotaCalls: 2, ResetEveryDays: 30));
        await gateway.AddClientAsync("Петров", ["echo"]);

        for (var call = 0; call < 3; call++)
        {
            using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        }

        await Eventually.TrueAsync(
            async () => (await CliRunner.RunAsync(gateway.DataDir, "usage", "log")).Output.Split('\n').Length > 3,
            "журнал дошёл до базы");
        var listed = await CliRunner.RunAsync(gateway.DataDir, "client", "list");
        var shown = await CliRunner.RunAsync(gateway.DataDir, "client", "show", "Иванов 4419");
        var log = await CliRunner.RunAsync(gateway.DataDir, "usage", "log");
        var lastLine = await CliRunner.RunAsync(gateway.DataDir, "usage", "log", "--limit", "1");
        var days = await CliRunner.RunAsync(gateway.DataDir, "usage", "days", "--client", "Иванов 4419");
        var idle = await CliRunner.RunAsync(gateway.DataDir, "usage", "log", "--client", "Петров");

        var lines = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("Иванов 4419") && line.Contains("исчерпан") && line.Contains("2/2"));
        Assert.Contains(lines, line => line.Contains("Петров") && line.Contains("активен") && line.Contains("0/∞"));
        Assert.Contains("Квота:   2 из 2 вызовов, обновится ", shown.Output);
        var logLines = log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, logLines.Length);
        Assert.Contains("квота исчерпана", logLines[0]);
        Assert.All(logLines, line => Assert.Contains("Иванов 4419", line));
        Assert.All(logLines, line => Assert.EndsWith("tools/call echo", line.TrimEnd()));
        Assert.Equal(2, logLines.Count(line => line.Contains("выполнен")));
        Assert.Contains("квота исчерпана", Assert.Single(lastLine.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Contains("вызовов 2, из них tools/call 2, сбоев upstream 0, отказов шлюза 1", days.Output);
        Assert.Equal("Журнал пуст", idle.Output.Trim());
    }

    [Fact]
    public async Task Usage_of_a_removed_client_stays_in_the_log()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        await Eventually.TrueAsync(
            async () => (await CliRunner.RunAsync(gateway.DataDir, "usage", "log")).Output.Contains("Иванов"),
            "журнал дошёл до базы");

        await CliRunner.RunAsync(gateway.DataDir, "client", "remove", "Иванов");
        await CliRunner.RunAsync(gateway.DataDir, "inbound", "remove", "echo");
        var log = await CliRunner.RunAsync(gateway.DataDir, "usage", "log");

        Assert.Equal(0, log.Code);
        Assert.DoesNotContain("Иванов", log.Output);
        Assert.Equal(2, log.Output.Split("удалён").Length - 1);
        Assert.Contains("tools/call echo", log.Output);
    }

    [Fact]
    public async Task Reset_from_the_command_line_gives_the_quota_back_on_the_running_server()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(QuotaCalls: 1));
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
        using var refused = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));

        var reset = await CliRunner.RunAsync(gateway.DataDir, "client", "reset", "Иванов");

        Assert.Contains("Лимит вызовов исчерпан", await refused.Content.ReadAsStringAsync());
        Assert.Equal(0, reset.Code);
        Assert.Contains("обнулён", reset.Output);
        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token, ToolCall));
                return !(await response.Content.ReadAsStringAsync()).Contains("Лимит вызовов исчерпан");
            },
            "после сброса из CLI вызовы снова проходят");
    }

    [Fact]
    public async Task Limit_raised_from_the_command_line_applies_on_the_running_server()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"], limits: new ClientLimits(RatePerMinute: 1));
        using var spent = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));

        var changed = await CliRunner.RunAsync(gateway.DataDir, "client", "set", "Иванов", "--rate", "none");

        Assert.Equal(0, changed.Code);
        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
                return response.IsSuccessStatusCode;
            },
            "снятый лимит применился");
    }
}
