using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using CtxUi.Connect.Registration;
using CtxUi.Connect.Resources;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Endpoints;

public static class TokenEndpoint
{
    public const string Path = ConnectServices.Prefix + "/token";

    public static readonly string[] Destinations = [OpenIddictConstants.Destinations.AccessToken];

    public static IEndpointRouteBuilder MapToken(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, ExchangeAsync).DisableAntiforgery();
        return app;
    }

    private static async Task<IResult> ExchangeAsync(
        HttpContext http,
        ISnapshotSource snapshot,
        DeviceService devices,
        RegisteredApps apps,
        TimeProvider clock)
    {
        var request = http.GetOpenIddictServerRequest()!;
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return ConnectResults.Refuse(Errors.UnsupportedGrantType,
                "only the authorization code and refresh token grants are supported");

        var authentication = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var presented = authentication.Principal!;
        if (!Guid.TryParse(presented.GetClaim(Claims.Subject), out var clientId)
            || !Guid.TryParse(presented.GetClaim(ConnectClaims.Device), out var deviceId))
            return Invalid();

        var current = snapshot.Current;
        if (ResourceHandlers.Match(current, request) is not { } resource)
            return ConnectResults.Refuse(Errors.InvalidTarget, ResourceHandlers.Refusal);

        var client = current.ClientById(clientId);
        if (client is null
            || !client.Enabled
            || (request.IsAuthorizationCodeGrantType()
                && client.ExpiresAt is { } expiresAt && clock.GetUtcNow() >= expiresAt)
            || current.Route(resource.Slug) is not { } inbound
            || !client.InboundIds.Contains(inbound.Id)
            || !await devices.IsActiveAsync(deviceId, http.RequestAborted))
            return Invalid();

        if (!presented.HasResource(resource.Url))
            return ConnectResults.Refuse(Errors.InvalidTarget, "resource must match the original grant");

        var identity = new ClaimsIdentity(presented.Claims, presented.Identity!.AuthenticationType, Claims.Name,
            Claims.Role);
        var principal = new ClaimsPrincipal(identity);
        if (principal.HasScope(Scopes.OfflineAccess)
            && !await apps.AllowsRefreshTokenAsync(request.ClientId!, http.RequestAborted))
            principal.SetScopes(principal.GetScopes().Remove(Scopes.OfflineAccess));
        principal.SetResources(resource.Url);
        principal.SetAudiences(resource.Url);
        principal.SetDestinations(_ => Destinations);
        principal.SetRefreshTokenLifetime(TimeSpan.FromDays(current.SignIn.IdleDays));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Invalid()
    {
        return ConnectResults.Refuse(Errors.InvalidGrant, "the grant is no longer valid");
    }
}
