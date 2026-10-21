using Microsoft.Extensions.Logging;

namespace CtxUi.IntegrationTests;

public class PanelServerLogTests
{
    [Fact]
    public async Task Neither_the_secret_path_nor_the_password_reach_the_server_log()
    {
        var logs = new CapturingLogs();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        var admin = await gateway.AddAdminAsync();
        using var panel = new PanelClient(gateway);

        using var wrong = await panel.SignInAsync("root", "not-the-password-1234");
        using var signedIn = await panel.SignInAsync("root", admin.Password);
        using var home = await panel.GetAsync("");
        using var missing = await panel.GetAsync("no-such-page");

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, line => line.Contains(gateway.PanelPath));
        Assert.DoesNotContain(logs.Lines, line => line.Contains(admin.Password));
        Assert.DoesNotContain(logs.Lines, line => line.Contains("not-the-password-1234"));
    }

    [Fact]
    public async Task Server_says_at_start_how_to_let_the_first_admin_in()
    {
        var logs = new CapturingLogs();
        await using var gateway = await GatewayHost.StartAsync(builder => builder.Logging.AddProvider(logs));
        Assert.Contains(logs.Lines, line => line.Contains("ctx-ui admin add"));

        await gateway.AddAdminAsync();
        logs.Lines.Clear();
        await gateway.RestartAsync();

        Assert.DoesNotContain(logs.Lines, line => line.Contains("ctx-ui admin add"));
    }
}
