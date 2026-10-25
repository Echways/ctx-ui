using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using CtxUi.Core.Clients;
using CtxUi.Core.SignIn;
using static CtxUi.IntegrationTests.ClientPages;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class PanelClientDevicesTests
{
    private static async Task<(GatewayHost Gateway, IssuedClient Issued, Device Device)> StartAsync(string signedInWith = "key")
    {
        var gateway = await StartWithoutPollingAsync();
        await gateway.AddInboundAsync("echo", "https://echo.example.org/mcp");
        var issued = await gateway.AddClientAsync("Иванов", ["echo"]);
        var device = await gateway.ChangeAsync(async services =>
        {
            var devices = services.GetRequiredService<DeviceService>();
            var admitted = (await devices.AdmitAsync(issued.Client.Id, DeviceMarker.Generate(), new DeviceSeen("Chrome · Windows", "203.0.113.7", signedInWith))).Device!;
            await devices.AddAppAsync(admitted.Id, "app-key", "Claude Code", "authorization-1");
            return await devices.GetAsync(admitted.Id);
        });
        return (gateway, issued, device);
    }

    private static Task<List<Device>> DevicesAsync(GatewayHost gateway, Guid clientId) => gateway.ChangeAsync(services =>
        services.GetRequiredService<DeviceService>().ListAsync(clientId, DateTimeOffset.MinValue));

    [Fact]
    public async Task Device_limit_and_direct_key_are_saved()
    {
        var (gateway, _, _) = await StartAsync();
        await using var _g = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await Row(page, "Иванов").GetByRole(AriaRole.Cell, new() { Name = "Иванов", Exact = true }).ClickAsync();
        await Dialog(page).GetByLabel("Лимит устройств").FillAsync("3");
        await Dialog(page).GetByLabel("Постоянный ключ работает").UncheckAsync();
        await SaveAsync(page);

        await Expect(Dialog(page)).ToHaveCountAsync(0);
        var stored = await StoredAsync(gateway, "Иванов");
        Assert.Equal(3, stored.MaxDevices);
        Assert.False(stored.DirectKey);
    }

    [Fact]
    public async Task Devices_dialog_lists_and_revokes()
    {
        var (gateway, issued, device) = await StartAsync();
        await using var _g = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Устройства");
        await Expect(Dialog(page)).ToContainTextAsync("Chrome · Windows");
        await Expect(Dialog(page)).ToContainTextAsync("Claude Code");
        await Expect(Dialog(page)).ToContainTextAsync("203.0.113.7");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отключить Chrome · Windows" }).ClickAsync();

        await Expect(page.GetByText("Устройство «Chrome · Windows» отключено")).ToBeVisibleAsync();
        var stored = Assert.Single(await DevicesAsync(gateway, issued.Client.Id));
        Assert.Equal(device.Id, stored.Id);
        Assert.Equal(DeviceRevocation.Admin, stored.RevokedReason);
    }

    [Fact]
    public async Task Revoked_device_shows_the_reason()
    {
        var (gateway, issued, device) = await StartAsync();
        await using var _g = gateway;
        await gateway.ChangeAsync(async services =>
        {
            await services.GetRequiredService<DeviceService>().RevokeAsync(issued.Client.Id, device.Id, DeviceRevocation.TokenReused);
            return true;
        });
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Устройства");

        await Expect(Dialog(page)).ToContainTextAsync("Отключено: повторно предъявлен уже использованный токен");
        await Expect(Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отключить Chrome · Windows" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Links_can_be_removed()
    {
        var (gateway, issued, _) = await StartAsync();
        await using var _g = gateway;
        await gateway.ChangeAsync(async services =>
        {
            var links = services.GetRequiredService<ClientLinkService>();
            await links.LinkAsync(await links.InviteAsync(issued.Client.Id), new ExternalIdentity("github", "42", "ivanov@example.org"));
            return true;
        });
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Устройства");
        await Expect(Dialog(page)).ToContainTextAsync("ivanov@example.org");
        await Dialog(page).GetByRole(AriaRole.Button, new() { Name = "Отвязать GitHub" }).ClickAsync();

        await Expect(page.GetByText("Аккаунт GitHub отвязан")).ToBeVisibleAsync();
        Assert.Empty(await gateway.ChangeAsync(services => services.GetRequiredService<ClientLinkService>().ListAsync(issued.Client.Id)));
    }

    [Fact]
    public async Task Reissue_confirmation_counts_key_devices()
    {
        var (gateway, _, _) = await StartAsync();
        await using var _g = gateway;
        await using var session = await PanelSession.SignInAsync(gateway);
        var page = await OpenClientsAsync(session);

        await RowMenu.ChooseAsync(Row(page, "Иванов"), "Новый токен");

        await Expect(page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Устройств, подключённых по ключу, будет отключено: 1.");
    }
}
