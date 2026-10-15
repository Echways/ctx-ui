using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Gateway;
using CtxUi.Panel;

namespace CtxUi.Server;

public static class ServerApp
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(ServerApp).Assembly.GetName().Name,
        });
        builder.WebHost.UseStaticWebAssets();
        configure?.Invoke(builder);
        builder.Logging.AddCtxUiFilters();
        builder.Services.AddCtxUi(builder.Configuration);
        builder.Services.AddCtxUiGateway(builder.Configuration.GetSection(CtxUiOptions.Section));
        builder.Services.AddCtxUiPanel();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseTokenPath();
        app.UseCtxUiPanelGate(GatewayServices.Path);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapCtxUiGateway();
        app.MapCtxUiPanel();
        return app;
    }

    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.MigrateAsync(ct);

        await app.Services.LoadCtxUiGatewayAsync(ct);
        await app.Services.LoadCtxUiPanelAsync(ct);
    }
}
