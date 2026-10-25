using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.SignIn;

namespace CtxUi.IntegrationTests;

public static class OAuthHarness
{
    public static async Task<GatewayHost> StartAsync(Action<WebApplicationBuilder>? configure = null)
    {
        var port = FreePort();
        var gateway = await GatewayHost.StartAsync(
            builder =>
            {
                builder.Configuration["CtxUi:PublicUrl"] = $"http://127.0.0.1:{port}";
                configure?.Invoke(builder);
            },
            port);
        await gateway.ArrangeAsync(services => services.GetRequiredService<SignInSettingsService>()
            .UpdateAsync(new SignInSettingsUpdate { Enabled = true }, gateway.Issuer));
        return gateway;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
