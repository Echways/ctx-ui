using System.Net;
using Microsoft.Data.Sqlite;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed class CliOAuthTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => CliRunner.RunAsync(_dataDir, args);

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
    public void Oauth_is_a_cli_command() => Assert.True(Cli.Handles(["oauth", "status"]));

    [Fact]
    public async Task Status_shows_oauth_off_by_default()
    {
        var status = await RunAsync("oauth", "status");

        Assert.Equal(0, status.Code);
        Assert.Contains("OAuth: выключен", status.Output);
        Assert.Contains("Вход по ключу: включён", status.Output);
        Assert.Contains("Провайдеров нет", status.Output);
    }

    [Fact]
    public async Task On_is_refused_for_an_http_public_url()
    {
        var refused = await CliRunner.RunWithPublicUrlAsync("http://mcp.example.org", _dataDir, "oauth", "on");

        Assert.Equal(1, refused.Code);
        Assert.Contains("https://", refused.Error);
    }

    [Fact]
    public async Task On_then_status_shows_key_sign_in_and_callbacks()
    {
        await RunAsync("oauth", "provider", "set", "github", "--client-id", "gh-id", "--client-secret", "gh-secret");

        var on = await RunAsync("oauth", "on");
        var status = await RunAsync("oauth", "status");

        Assert.Equal(0, on.Code);
        Assert.Contains("OAuth: включён", status.Output);
        Assert.Contains("Вход по ключу: включён", status.Output);
        Assert.Contains("github", status.Output);
        Assert.Contains("https://mcp.example.org/connect/callback/github", status.Output);
    }

    [Fact]
    public async Task Key_off_is_refused_while_no_provider_is_left()
    {
        await RunAsync("oauth", "on");

        var refused = await RunAsync("oauth", "key", "off");

        Assert.Equal(1, refused.Code);
        Assert.Contains("хотя бы один способ входа", refused.Error);
    }

    [Fact]
    public async Task Provider_set_reads_the_secret_from_stdin()
    {
        var first = await CliRunner.RunWithInputAsync("from-stdin\n", _dataDir, "oauth", "provider", "set", "github", "--client-id", "gh-id", "--client-secret-stdin");
        var again = await RunAsync("oauth", "provider", "set", "github", "--client-id", "gh-id-2");
        var listed = await RunAsync("oauth", "provider", "list");

        Assert.Equal(0, first.Code);
        Assert.Equal(0, again.Code);
        Assert.Contains("gh-id-2", listed.Output);
    }

    [Fact]
    public async Task Provider_set_without_a_secret_for_a_new_provider_fails()
    {
        var refused = await RunAsync("oauth", "provider", "set", "github", "--client-id", "gh-id");

        Assert.Equal(1, refused.Code);
        Assert.Contains("client secret", refused.Error);
    }

    [Fact]
    public async Task Off_warns_about_clients_without_direct_key()
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://echo.example.org/mcp");
        await RunAsync("client", "add", "Иванов", "--inbound", "echo", "--direct-key", "off");
        await RunAsync("oauth", "on");

        var off = await RunAsync("oauth", "off");

        Assert.Equal(0, off.Code);
        Assert.Contains("клиентов без постоянного ключа: 1", off.Output);
        Assert.Contains("OAuth выключен", off.Output);
    }

    [Fact]
    public async Task Change_from_cli_reaches_the_running_server()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        using var before = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");

        var on = await CliRunner.RunAsync(gateway.DataDir, "oauth", "on");

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        Assert.Equal(0, on.Code);
        await Eventually.TrueAsync(
            async () =>
            {
                using var after = await gateway.Http.GetAsync("/.well-known/oauth-protected-resource/mcp/echo");
                return after.StatusCode == HttpStatusCode.OK;
            },
            "метаданные OAuth стали отвечать");
    }
}
