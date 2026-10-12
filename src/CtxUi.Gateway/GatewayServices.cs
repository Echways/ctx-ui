using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddSingleton<GatewayHandler>();
        return services;
    }

    /// <summary>Порядок важен: токен вырезается из пути до маршрутизации.</summary>
    public static WebApplication UseCtxUiGateway(this WebApplication app)
    {
        app.UseTokenPath();
        app.UseRouting();
        app.Map("/mcp/{slug}", app.Services.GetRequiredService<GatewayHandler>().HandleAsync);
        return app;
    }
}
