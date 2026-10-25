using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using CtxUi.Connect;
using CtxUi.Core.Data;
using CtxUi.Gateway;
using CtxUi.Panel;
using CtxUi.Server.Api;

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
        builder.Services.AddCtxUiPanel(builder.Configuration.GetSection(CtxUiOptions.Section));
        builder.Services.AddCtxUiConnect(
            builder.Configuration.GetSection(CtxUiOptions.Section),
            ServiceRegistration.KeysPath(builder.Configuration));
        builder.Services.AddCtxUiApi();
        builder.Services.AddHealthChecks().AddDbContextCheck<CtxUiDbContext>();
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseForwardedHeaders(ForwardedFor(app.Services.GetRequiredService<IOptions<CtxUiOptions>>().Value.TrustedProxies));
        app.UseTokenPath();
        app.UseCtxUiConnectGate();
        app.UseCtxUiPanelGate([GatewayServices.Path, HealthProbe.Path, .. ConnectServices.OpenPaths]);
        app.UseCtxUiPanelLanguage();
        app.UseCtxUiApiDocs();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapCtxUiGateway();
        app.MapCtxUiApi();
        app.MapCtxUiConnect();
        app.MapCtxUiPanel();
        app.MapHealthChecks(HealthProbe.Path);
        return app;
    }

    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().PrepareAsync(ct);

        await app.Services.LoadCtxUiGatewayAsync(ct);
        app.Services.LoadCtxUiConnect();
        await app.Services.LoadCtxUiPanelAsync(ct);
    }

    private static ForwardedHeadersOptions ForwardedFor(string trustedProxies)
    {
        var (proxies, networks) = ProxyTrust.Parse(trustedProxies);
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in proxies)
            options.KnownProxies.Add(proxy);
        foreach (var network in networks)
            options.KnownIPNetworks.Add(network);

        return options;
    }
}
