using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public sealed class CliDevicesTests : IDisposable
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

    private async Task PrepareAsync(params string[] clientArgs)
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://echo.example.org/mcp");
        var added = await RunAsync(["client", "add", "Иванов", "--inbound", "echo", .. clientArgs]);
        Assert.Equal(0, added.Code);
    }

    private static async Task<Device> AdmitAsync(GatewayHost gateway, Guid clientId, string name, string signedInWith = Device.ByKey) =>
        await gateway.ChangeAsync(async services =>
        {
            var devices = services.GetRequiredService<DeviceService>();
            var admitted = (await devices.AdmitAsync(clientId, DeviceMarker.Generate(), new DeviceSeen(name, "203.0.113.7", signedInWith))).Device!;
            await devices.AddAppAsync(admitted.Id, "app-key", "Claude Code", "authorization-1");
            return await devices.GetAsync(admitted.Id);
        });

    [Fact]
    public async Task Usage_log_shows_the_device()
    {
        await using var upstream = await FakeUpstream.StartAsync();
        await using var gateway = await OAuthHarness.StartAsync();
        await gateway.AddInboundAsync("echo", upstream.McpUrl);
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        using var flow = await OAuthFlow.StartAsync(gateway, "echo");
        flow.Browser.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        await flow.SignInWithKeyAsync(issued.Token);
        await flow.ApproveAsync();
        var tokens = await flow.ExchangeAsync();
        using var call = await gateway.Http.SendAsync(OAuthFlow.Mcp("echo", tokens.Access, ClientPages.ToolCall));
        await Eventually.TrueAsync(
            async () => (await CliRunner.RunAsync(gateway.DataDir, "usage", "log")).Output.Contains("Chrome · Windows"),
            "журнал с устройством дошёл до базы");

        var log = await CliRunner.RunAsync(gateway.DataDir, "usage", "log");

        Assert.Single(log.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("Иванов", log.Output);
    }

    [Fact]
    public async Task Add_with_devices_and_direct_key_off()
    {
        await PrepareAsync("--devices", "2", "--direct-key", "off");

        var shown = await RunAsync("client", "show", "Иванов");

        Assert.Contains("Лимит устройств: 2", shown.Output);
        Assert.Contains("Постоянный ключ: нет", shown.Output);
        Assert.Contains("Устройства: 0 активных", shown.Output);
    }

    [Fact]
    public async Task Add_refuses_none_for_devices()
    {
        await RunAsync("inbound", "add", "echo", "--url", "https://echo.example.org/mcp");

        var refused = await RunAsync("client", "add", "Иванов", "--inbound", "echo", "--devices", "none");

        Assert.Equal(1, refused.Code);
    }

    [Fact]
    public async Task Set_devices_none_removes_the_limit()
    {
        await PrepareAsync("--devices", "2");

        var changed = await RunAsync("client", "set", "Иванов", "--devices", "none");
        var shown = await RunAsync("client", "show", "Иванов");

        Assert.Equal(0, changed.Code);
        Assert.Contains("Лимит устройств: без ограничения", shown.Output);
    }

    [Fact]
    public async Task Devices_lists_and_device_off_revokes_by_prefix()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var device = await AdmitAsync(gateway, issued.Client.Id, "Chrome · Windows");
        var prefix = device.Id.ToString()[..8];

        var listed = await CliRunner.RunAsync(gateway.DataDir, "client", "devices", "Иванов");
        var off = await CliRunner.RunAsync(gateway.DataDir, "client", "device-off", "Иванов", prefix);
        var after = await CliRunner.RunAsync(gateway.DataDir, "client", "devices", "Иванов");
        var missing = await CliRunner.RunAsync(gateway.DataDir, "client", "device-off", "Иванов", "zzzzzzzz");

        Assert.Contains(prefix, listed.Output);
        Assert.Contains("Chrome · Windows", listed.Output);
        Assert.Contains("Claude Code", listed.Output);
        Assert.Contains("203.0.113.7", listed.Output);
        Assert.Contains("активно", listed.Output);
        Assert.Equal(0, off.Code);
        Assert.Contains("отключено администратором", after.Output);
        Assert.Equal(1, missing.Code);
    }

    [Fact]
    public async Task Device_off_all_revokes_every_device()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await AdmitAsync(gateway, issued.Client.Id, "Chrome · Windows");

        var off = await CliRunner.RunAsync(gateway.DataDir, "client", "device-off", "Иванов", "--all");
        var after = await CliRunner.RunAsync(gateway.DataDir, "client", "devices", "Иванов");

        Assert.Equal(0, off.Code);
        Assert.Contains("отключено администратором", after.Output);
    }

    [Fact]
    public async Task Invite_prints_a_link_that_expires_in_seven_days()
    {
        await PrepareAsync();

        var invite = await RunAsync("client", "invite", "Иванов");

        Assert.Equal(0, invite.Code);
        Assert.Contains("https://mcp.example.org/connect/invite/", invite.Output);
        Assert.Contains(DateTimeOffset.UtcNow.AddDays(7).ToString("yyyy-MM-dd"), invite.Output);
    }

    [Fact]
    public async Task Links_are_listed_and_removed()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await gateway.ChangeAsync(async services =>
        {
            var links = services.GetRequiredService<ClientLinkService>();
            await links.LinkAsync(await links.InviteAsync(issued.Client.Id), new ExternalIdentity("github", "42", "ivanov@example.org"));
            return true;
        });

        var listed = await CliRunner.RunAsync(gateway.DataDir, "client", "links", "Иванов");
        var removed = await CliRunner.RunAsync(gateway.DataDir, "client", "unlink", "Иванов", "github");
        var after = await CliRunner.RunAsync(gateway.DataDir, "client", "links", "Иванов");

        Assert.Contains("ivanov@example.org", listed.Output);
        Assert.Equal(0, removed.Code);
        Assert.Contains("Привязок нет", after.Output);
    }

    [Fact]
    public async Task Reissue_reports_revoked_key_devices()
    {
        await using var gateway = await GatewayHost.StartAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        await AdmitAsync(gateway, issued.Client.Id, "Chrome · Windows");

        var reissued = await CliRunner.RunAsync(gateway.DataDir, "client", "reissue", "Иванов");

        Assert.Contains("Отключено устройств: 1", reissued.Output);
    }
}
