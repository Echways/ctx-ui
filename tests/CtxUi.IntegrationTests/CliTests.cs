using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed partial class CliTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;

    [GeneratedRegex("tnk_[A-Za-z0-9]{32}")]
    private static partial Regex TokenPattern();

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => RunInAsync(_dataDir, args);

    private static Task<(int Code, string Output, string Error)> RunInAsync(string dataDir, params string[] args) =>
        CliRunner.RunAsync(dataDir, args);

    private async Task<string> IssueAsync(string name, params string[] extra)
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");
        var added = await RunAsync(["client", "add", name, "--inbound", "echo", .. extra]);
        Assert.Equal("", added.Error);
        return TokenPattern().Match(added.Output).Value;
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
    public async Task Cli_speaks_english_by_default()
    {
        var shown = await CliRunner.RunInLanguageAsync("en", _dataDir, "client", "show", "nobody");

        Assert.Equal(1, shown.Code);
        Assert.Equal("Error: client “nobody” not found", shown.Error.Trim());
    }

    [Fact]
    public void Only_inbound_and_client_are_cli_commands()
    {
        Assert.True(Cli.Handles(["inbound", "list"]));
        Assert.True(Cli.Handles(["client"]));
        Assert.False(Cli.Handles([]));
        Assert.False(Cli.Handles(["--urls", "http://127.0.0.1:9000"]));
    }

    [Fact]
    public async Task Inbound_can_be_added_and_listed()
    {
        var added = await RunAsync(
            "inbound", "add", "echo", "--url", "https://upstream.example.org/mcp", "--header", "X-Api-Key=k1", "--forward-identity");
        var listed = await RunAsync("inbound", "list");

        Assert.Equal(0, added.Code);
        Assert.Contains("https://mcp.example.org/mcp/echo", added.Output);
        Assert.Contains("echo", listed.Output);
        Assert.Contains("включён", listed.Output);
        Assert.Contains("https://upstream.example.org/mcp", listed.Output);
        Assert.DoesNotContain("k1", listed.Output);
    }

    [Fact]
    public async Task Issued_client_gets_a_token_and_ready_addresses()
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");

        var added = await RunAsync(
            "client", "add", "Иванов 4419", "--inbound", "echo", "--expires", "2099-01-01", "--meta", "group=4419");

        Assert.Equal(0, added.Code);
        var token = TokenPattern().Match(added.Output).Value;
        Assert.NotEqual("", token);
        Assert.Contains("Иванов 4419", added.Output);
        Assert.Contains("до 2099-01-01", added.Output);
        Assert.Contains("https://mcp.example.org/mcp/echo", added.Output);
        Assert.Contains($"https://mcp.example.org/k/{token}/mcp/echo", added.Output);
    }

    [Fact]
    public async Task Client_list_shows_status_expiry_and_inbounds()
    {
        await IssueAsync("Иванов 4419", "--expires", "2099-01-01");
        await IssueAsync("Петров");
        await RunAsync("client", "disable", "Петров");

        var listed = await RunAsync("client", "list");

        var lines = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.Contains("Иванов 4419") && line.Contains("активен") && line.Contains("2099-01-01") && line.Contains("echo"));
        Assert.Contains(lines, line => line.Contains("Петров") && line.Contains("отключён") && line.Contains("бессрочно"));
    }

    [Fact]
    public async Task Show_repeats_the_token_and_reissue_replaces_it()
    {
        var token = await IssueAsync("Иванов");

        var shown = await RunAsync("client", "show", "иванов");
        var reissued = await RunAsync("client", "reissue", "Иванов");
        var shownAgain = await RunAsync("client", "show", "Иванов");

        Assert.Contains(token, shown.Output);
        var replacement = TokenPattern().Match(reissued.Output).Value;
        Assert.NotEqual(token, replacement);
        Assert.Contains(replacement, shownAgain.Output);
        Assert.DoesNotContain(token, shownAgain.Output);
    }

    [Fact]
    public async Task Set_changes_expiry_and_inbounds()
    {
        await IssueAsync("Иванов", "--expires", "2099-01-01");
        await RunAsync("inbound", "add", "docs", "--url", "https://docs.example.org/mcp");

        var changed = await RunAsync("client", "set", "Иванов", "--expires", "never", "--inbound", "docs");
        var listed = await RunAsync("client", "list");

        Assert.Equal(0, changed.Code);
        Assert.Contains("бессрочно", listed.Output);
        Assert.Contains("docs", listed.Output);
        Assert.DoesNotContain("echo", listed.Output);
    }

    [Theory]
    [InlineData("client|add|Иванов|--inbound|echo")]
    [InlineData("client|add|Петров|--inbound|nope")]
    [InlineData("client|add|Петров")]
    [InlineData("client|add|Петров|--inbound|echo|--expires|01.07.2027")]
    [InlineData("client|add|Петров|--inbound|echo|--meta|без-знака-равно")]
    [InlineData("client|show|Сидоров")]
    [InlineData("inbound|add|echo|--url|https://other.example.org/mcp")]
    [InlineData("inbound|add|Плохой Slug|--url|https://other.example.org/mcp")]
    public async Task Rule_violation_is_explained_and_exits_with_one(string commandLine)
    {
        await IssueAsync("Иванов");

        var result = await RunAsync(commandLine.Split('|'));

        Assert.Equal(1, result.Code);
        Assert.StartsWith("Ошибка: ", result.Error);
        Assert.Equal("", result.Output);
    }

    [Fact]
    public async Task Missing_required_option_exits_with_one()
    {
        var result = await RunAsync("inbound", "add", "echo");

        Assert.Equal(1, result.Code);
        Assert.NotEqual("", result.Error);
    }

    [Fact]
    public async Task Show_says_what_to_do_when_the_keys_directory_is_lost()
    {
        var token = await IssueAsync("Иванов");
        Directory.Delete(Path.Combine(_dataDir, "keys"), recursive: true);

        var shown = await RunAsync("client", "show", "Иванов");

        Assert.Equal(0, shown.Code);
        Assert.DoesNotContain(token, shown.Output);
        Assert.Contains("показать нельзя", shown.Output);
        Assert.Contains("client reissue", shown.Output);
    }

    [Fact]
    public async Task Running_server_applies_a_change_made_from_the_command_line()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);

        var disabled = await RunInAsync(gateway.DataDir, "client", "disable", "Иванов");

        Assert.Equal(0, disabled.Code);
        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await gateway.Http.SendAsync(GatewayHost.Post("/mcp/echo", issued.Token));
                return response.StatusCode == HttpStatusCode.Unauthorized;
            },
            "сервер увидел отключение клиента из CLI");
    }
}
