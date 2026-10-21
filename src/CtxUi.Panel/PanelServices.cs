using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using CtxUi.Core;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;
using CtxUi.Core.Settings;
using CtxUi.Panel.Accounts;
using CtxUi.Panel.Components;

namespace CtxUi.Panel;

public static class PanelServices
{
    public const int MaxFailedSignIns = 5;

    public static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

    public static IServiceCollection AddCtxUiPanel(this IServiceCollection services, IConfiguration section)
    {
        services.Configure<PanelOptions>(section);
        var sessionCheck = section.Get<PanelOptions>()?.SessionCheckInterval ?? new PanelOptions().SessionCheckInterval;
        services.AddSingleton<PanelGate>();
        services.AddSingleton<PanelBackend>();
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
        services.AddCascadingAuthenticationState();
        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddScoped<AuthenticationStateProvider, AdminSessionState>();
        services.AddMudServices(mud =>
        {
            mud.SnackbarConfiguration.PositionClass = MudBlazor.Defaults.Classes.Position.BottomLeft;
            mud.SnackbarConfiguration.HideIcon = true;
            mud.SnackbarConfiguration.ShowCloseIcon = false;
            mud.SnackbarConfiguration.ShowTransitionDuration = 160;
            mud.SnackbarConfiguration.HideTransitionDuration = 160;
        });
        services.AddScoped<Clipboard>();
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture(PanelLanguage.Default)
                .AddSupportedCultures(PanelLanguage.Cultures)
                .AddSupportedUICultures(PanelLanguage.Cultures);
            options.RequestCultureProviders = [new CookieRequestCultureProvider()];
        });
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
        app.MapStaticAssets();
        app.MapGet("/language/{culture}", PanelLanguage.Choose);
        app.MapRazorComponents<App>().AddInteractiveServerRenderMode().RequireAuthorization();
        return app;
    }

    public static async Task LoadCtxUiPanelAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var path = await scope.ServiceProvider.GetRequiredService<PanelSettings>().PathAsync(ct);
        services.GetRequiredService<PanelGate>().Open(path);
        if (!await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Admins.AnyAsync(ct))
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("CtxUi.Panel").LogWarning(
                "nobody can sign in to the panel: there are no admins. Add the first one with ctx-ui admin add <login>");
        }
    }
}
