using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Admins;

namespace CtxUi.Panel.Accounts;

public sealed class AdminSessionState(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopes,
    IOptions<IdentityOptions> identity,
    IOptions<PanelOptions> panel) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => panel.Value.SessionCheckInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<Admin>>();
        var admin = await users.GetUserAsync(authenticationState.User);
        var stamp = authenticationState.User.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType);
        return admin is not null && stamp == admin.SecurityStamp;
    }
}
