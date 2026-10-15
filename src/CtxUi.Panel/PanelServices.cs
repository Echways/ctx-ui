using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
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
            options.Cookie.Name = "ctx-ui";
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
        services.AddMudServices();
        return services;
    }

    public static IApplicationBuilder UseCtxUiPanelGate(this IApplicationBuilder app, params PathString[] openPaths)
    {
        var gate = app.ApplicationServices.GetRequiredService<PanelGate>();
        return app.Use(next => context => gate.PassAsync(context, next, openPaths));
    }

    public static WebApplication MapCtxUiPanel(this WebApplication app)
    {
        app.MapStaticAssets();
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
                "войти в панель некому: администраторов нет. Добавьте первого командой ctx-ui admin add <логин>");
        }
    }
}
