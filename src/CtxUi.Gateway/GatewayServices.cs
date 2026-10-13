using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;

namespace CtxUi.Gateway;

public static class GatewayServices
{
    public static IServiceCollection AddCtxUiGateway(this IServiceCollection services, IConfiguration section)
    {
        services.Configure<GatewayOptions>(section);
        services.AddHttpForwarder();
        services.AddSingleton<SnapshotStore>();
        services.AddHostedService<SnapshotRefresher>();
        services.AddSingleton<HttpUpstream>();
        services.AddSingleton<ClientTouch>();
        services.AddSingleton<ClientLimiter>();
        services.AddSingleton<QuotaLedger>();
        services.AddHostedService<QuotaSaver>();
        services.AddSingleton<GatewayHandler>();
        return services;
    }

    public static WebApplication UseCtxUiGateway(this WebApplication app)
    {
        app.UseTokenPath();
        app.UseRouting();
        app.Map("/mcp/{slug}", app.Services.GetRequiredService<GatewayHandler>().HandleAsync);
        return app;
    }

    public static async Task LoadCtxUiGatewayAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<ClientService>().LoadUsageAsync(ct);
            services.GetRequiredService<QuotaLedger>().Restore(stored);
        }

        await services.GetRequiredService<SnapshotStore>().ReloadAsync(ct);
    }
}
