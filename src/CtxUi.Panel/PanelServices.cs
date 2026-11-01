using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Enfilade;
using CtxUi.Extensions;
using CtxUi.Core;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;
using CtxUi.Core.Settings;
using CtxUi.Panel.Accounts;
using CtxUi.Panel.Components.Pages;

namespace CtxUi.Panel;

public static partial class PanelServices
{
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    private const string Brand = "ctx-ui";

    private const string Favicon = "_content/CtxUi.Panel/favicon.svg";

    internal static PanelLayout CreateLayout() =>
        new PanelLayout()
            .AddBuiltIn<Overview>(PanelSections.Overview, () => PanelText.Overview)
            .AddBuiltIn<Metrics>(PanelSections.Metrics, () => PanelText.Metrics)
            .AddBuiltIn<Inbounds>(PanelSections.Inbounds, () => PanelText.Inbounds)
            .AddBuiltIn<Clients>(PanelSections.Clients, () => PanelText.Clients)
            .AddBuiltIn<Journal>(PanelSections.Journal, () => PanelText.Journal)
            .AddBuiltInCaption(PanelSections.AccessCaption, () => PanelText.AccessSection)
            .AddBuiltIn<OAuth>(PanelSections.OAuth, () => PanelText.OAuth)
            .AddBuiltIn<Keys>(PanelSections.ApiKeys, () => PanelText.ApiKeys)
            .AddBuiltInStylesheet("_content/CtxUi.Panel/panel.css");

    internal static IServiceCollection AddCtxUiPanel(this IServiceCollection services, IConfiguration section, Action<PanelLayout> extend)
    {
        services.Configure<PanelOptions>(section);
        var sessionCheck = section.Get<PanelOptions>()?.SessionCheckInterval ?? new PanelOptions().SessionCheckInterval;
        services.AddSingleton<PanelGate>();
        services.AddSingleton<PanelBackend>();
        services.AddSingleton<IPanelData, PanelData>();
        services.AddScoped<PanelActions>();
        services.AddScoped<IBlockSheets, BlockSheets>();
        services.AddIdentityCore<Admin>(options =>
            {
                options.Lockout.MaxFailedAccessAttempts = MaxFailedSignIns;
                options.Lockout.DefaultLockoutTimeSpan = Lockout;
            })
            .AddUserStore<AdminStore>()
            .AddSignInManager();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = OwnCookies.Session;
            options.LoginPath = "/login";
            options.LogoutPath = "/logout";
            options.AccessDeniedPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(7);
            options.SlidingExpiration = true;
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = sessionCheck);
        services.AddAuthorization();
        services.AddOptions<EnfiladeOptions>().Configure<IOptions<PanelOptions>>((shell, panel) =>
        {
            shell.Brand = Brand;
            shell.Favicon = Favicon;
            shell.Language = panel.Value.Language;
            shell.Languages = [.. Languages.Supported];
            shell.LoginPath = "login";
            shell.SignOutPath = "logout";
            shell.AutoRefreshInterval = panel.Value.AutoRefreshInterval;
        });
        var layout = CreateLayout();
        extend(layout);
        layout.Apply(services.AddEnfilade());
        services.AddSingleton(layout);
        services.AddScoped<AuthenticationStateProvider, AdminSessionState>();
        return services;
    }

    public static IApplicationBuilder UseCtxUiPanelGate(this IApplicationBuilder app, params PathString[] openPaths)
    {
        var gate = app.ApplicationServices.GetRequiredService<PanelGate>();
        return app.Use(next => context => gate.PassAsync(context, next, openPaths));
    }

    public static IApplicationBuilder UseCtxUiPanelLanguage(this IApplicationBuilder app) => app.UseRequestLocalization();

    public static WebApplication MapCtxUiPanel(this WebApplication app)
    {
        app.MapEnfilade();
        return app;
    }

    public static async Task LoadCtxUiPanelAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var path = await scope.ServiceProvider.GetRequiredService<PanelSettings>().PathAsync(ct);
        services.GetRequiredService<PanelGate>().Open(path);
        if (!await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Admins.AnyAsync(ct))
        {
            NoAdmins(services.GetRequiredService<ILoggerFactory>().CreateLogger("CtxUi.Panel"));
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "nobody can sign in to the panel: there are no admins. Add the first one with ctx-ui admin add <login>")]
    private static partial void NoAdmins(ILogger logger);
}
