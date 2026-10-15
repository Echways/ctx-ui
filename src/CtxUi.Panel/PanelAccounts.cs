using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Admins;
using CtxUi.Panel.Accounts;

namespace CtxUi.Panel;

public static class PanelAccounts
{
    public static IServiceCollection AddCtxUiAccounts(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<Admin>, PasswordHasher<Admin>>();
        services.AddScoped<AdminAccounts>();
        return services;
    }
}
