using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using CtxUi.Connect;
using CtxUi.Core.Data;
using CtxUi.Gateway;
using CtxUi.Panel;
using CtxUi.Server.Api;
using CtxUi.Server.Extensions;

namespace CtxUi.Server;

public static partial class ServerApp
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
        var section = builder.Configuration.GetSection(CtxUiOptions.Section);
        var extensions = ExtensionCatalog.Load(section.Get<CtxUiOptions>() ?? new CtxUiOptions());
        builder.Services.AddSingleton(extensions);
        builder.Services.AddCtxUiPanel(section, layout => ExtensionPipeline.Register(layout, builder.Services, extensions, section));
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
        app.UseExtensionAssets(extensions);
        app.UseCtxUiApiDocs();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCtxUiGateway();
        app.MapCtxUiApi();
        app.MapCtxUiConnect();
        app.MapExtensionEndpoints(extensions);
        app.MapCtxUiPanel();
        app.MapHealthChecks(HealthProbe.Path);
        return app;
    }

    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        ReportRejectedExtensions(app);
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().PrepareAsync(ct);

        await app.Services.LoadCtxUiGatewayAsync(ct);
        app.Services.LoadCtxUiConnect();
        await app.Services.LoadCtxUiPanelAsync(ct);
        await ExtensionPipeline.InitializeExtensionsAsync(app.Services, ct);
    }

    private static void ReportRejectedExtensions(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ExtensionCatalog));
        foreach (var rejected in app.Services.GetRequiredService<ExtensionCatalog>().Rejected)
        {
            if (rejected.Reason == ExtensionCatalog.Disabled)
                LogExtensionDisabled(logger, rejected.Name);
            else
                LogExtensionSkipped(logger, rejected.Name, rejected.Reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Extension {Name} is disabled")]
    private static partial void LogExtensionDisabled(ILogger logger, string name);

    [LoggerMessage(Level = LogLevel.Error, Message = "Extension {Name} skipped: {Reason}")]
    private static partial void LogExtensionSkipped(ILogger logger, string name, string reason);

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
