using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
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
        ResourceHandlers resources,
        ISnapshotSource snapshot,
        DeviceService devices,
        TimeProvider clock)
    {
        var request = http.GetOpenIddictServerRequest()!;
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
            return ConnectResults.Refuse(Errors.UnsupportedGrantType, "only the authorization code and refresh token grants are supported");

        var authentication = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var presented = authentication.Principal!;
        if (!Guid.TryParse(presented.GetClaim(Claims.Subject), out var clientId)
            || !Guid.TryParse(presented.GetClaim(ConnectClaims.Device), out var deviceId))
            return Invalid();

        var client = snapshot.Current.ClientById(clientId);
        if (client is null
            || !client.Enabled
            || client.ExpiresAt is { } expiresAt && clock.GetUtcNow() >= expiresAt
            || resources.ServedSlugOf(request) is not { } slug
            || snapshot.Current.Route(slug) is not { } route
            || !client.InboundIds.Contains(route.Id)
            || !await devices.IsActiveAsync(deviceId, http.RequestAborted))
            return Invalid();

        var identity = new ClaimsIdentity(presented.Claims, presented.Identity!.AuthenticationType, Claims.Name, Claims.Role);
        var principal = new ClaimsPrincipal(identity);
        principal.SetResources(resources.Resource(slug));
        principal.SetAudiences(resources.Resource(slug));
        principal.SetDestinations(_ => Destinations);
        principal.SetRefreshTokenLifetime(TimeSpan.FromDays(snapshot.Current.SignIn.IdleDays));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static IResult Invalid() => ConnectResults.Refuse(Errors.InvalidGrant, "the grant is no longer valid");
}
