using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed partial class CliApiKeyTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;

    [GeneratedRegex(@"Ключ:     (ctxk_[A-Za-z0-9]{40})\b")]
    private static partial Regex KeyLine();

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
    public void Apikey_is_a_cli_command() => Assert.True(Cli.Handles(["apikey", "list"]));

    [Fact]
    public async Task Added_key_is_shown_once_with_the_api_and_swagger_addresses()
    {
        var added = await RunAsync("apikey", "add", "bot");
        var path = (await RunAsync("admin", "path")).Output.Trim()["Панель:  ".Length..];

        Assert.Equal(0, added.Code);
        Assert.Equal("", added.Error);
        Assert.Contains("Ключ «bot» выпущен", added.Output);
        Assert.Matches(KeyLine(), added.Output);
        Assert.Contains($"API:      {path}api/v1/", added.Output);
        Assert.Contains($"Swagger:  {path}swagger/", added.Output);
    }

    [Fact]
    public async Task List_shows_only_the_prefix()
    {
        var key = KeyLine().Match((await RunAsync("apikey", "add", "bot")).Output).Groups[1].Value;

        var listed = await RunAsync("apikey", "list");

        Assert.Contains($"{key[..11]}…", listed.Output);
        Assert.DoesNotContain(key, listed.Output);
    }

    [Fact]
    public async Task Empty_list_says_how_to_add_a_key()
    {
        var listed = await RunAsync("apikey", "list");

        Assert.Equal(0, listed.Code);
        Assert.Contains("ctx-ui apikey add <название>", listed.Output);
    }

    [Fact]
    public async Task Duplicate_and_unknown_names_are_errors()
    {
        await RunAsync("apikey", "add", "bot");

        var duplicate = await RunAsync("apikey", "add", "BOT");
        var unknown = await RunAsync("apikey", "remove", "nope");

        Assert.Equal(1, duplicate.Code);
        Assert.Contains("Ошибка: ключ «BOT» уже есть", duplicate.Error);
        Assert.Equal(1, unknown.Code);
        Assert.Contains("Ошибка: ключ «nope» не найден", unknown.Error);
    }

    [Fact]
    public async Task Key_added_and_removed_from_the_cli_opens_and_closes_a_running_server()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var key = KeyLine().Match((await CliRunner.RunAsync(gateway.DataDir, "apikey", "add", "bot")).Output).Groups[1].Value;
        using var api = ApiClient.WithKey(gateway, key);

        using var before = await api.GetAsync("api/v1/inbounds");
        var removed = await CliRunner.RunAsync(gateway.DataDir, "apikey", "remove", "bot");
        using var after = await api.GetAsync("api/v1/inbounds");

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Contains("Ключ «bot» отозван", removed.Output);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }
}
