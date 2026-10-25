using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using CtxUi.Connect.Pages;
using CtxUi.Connect.Registration;
using CtxUi.Connect.Resources;
using CtxUi.Connect.SignIn;
using CtxUi.Core.Data;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Endpoints;

public static class AuthorizeEndpoint
{
    public const string Path = ConnectServices.Prefix + "/authorize";

    public static readonly string[] GrantedScopes = [ConnectServices.McpScope, Scopes.OfflineAccess];

    public static IEndpointRouteBuilder MapAuthorize(this IEndpointRouteBuilder app)
    {
        app.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], AuthorizeAsync).DisableAntiforgery();
        app.MapPost(ConnectServices.Prefix + "/consent", ConsentAsync);
        app.MapPost(ConnectServices.Prefix + "/limit/replace", ReplaceAsync);
        return app;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> Forwarded(OpenIddictRequest request) =>
        request.GetParameters()
            .Where(parameter => parameter.Key != ConsentPage.DenyParameter)
            .SelectMany(parameter => ValuesOf(parameter.Value).Select(value => KeyValuePair.Create(parameter.Key, value)))
            .ToList();

    private static IEnumerable<string> ValuesOf(OpenIddictParameter parameter) =>
        parameter.GetUnnamedParameters() is { Count: > 0 } unnamed
            ? unnamed.Select(value => value.ToString() ?? "")
            : [parameter.ToString() ?? ""];

    public static string ReturnUrl(OpenIddictRequest request) =>
        Path + QueryString.Create(Forwarded(request).Select(pair => KeyValuePair.Create(pair.Key, (string?)pair.Value)));

    private static async Task<IResult> AuthorizeAsync(
        HttpContext http,
        ResourceHandlers resources,
        ISnapshotSource snapshot,
        DeviceService devices,
        RegisteredApps apps,
        IOpenIddictAuthorizationManager authorizations,
        CtxUiDbContext db,
        TimeProvider clock)
    {
        var request = http.GetOpenIddictServerRequest()!;
        if (request.HasParameter(ConsentPage.DenyParameter))
            return ConnectResults.Refuse(Errors.AccessDenied, "the user did not allow the access");
        if (resources.ServedSlugOf(request) is not { } slug)
            return ConnectResults.Refuse(Errors.InvalidTarget, ResourceHandlers.Refusal);

        var returnUrl = ReturnUrl(request);
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, returnUrl);

        var client = signedIn.Client;
        if (!client.Enabled)
            return ConnectResults.Notice(ConnectText.ClientDisabled);
        if (client.ExpiresAt is { } expiresAt && clock.GetUtcNow() >= expiresAt)
            return ConnectResults.Notice(ConnectText.ClientExpired);
        if (!client.InboundIds.Contains(snapshot.Current.Route(slug)!.Id))
            return ConnectResults.Notice(string.Format(ConnectText.NoAccessToInbound, client.Name));

        var admission = await devices.AdmitAsync(client.Id, DeviceMarkerCookie.GetOrIssue(http), signedIn.Seen(http));
        if (admission.Device is not { } device)
        {
            return ConnectResults.Page<LimitPage>(new
            {
                Devices = admission.Active,
                Limit = client.MaxDevices ?? admission.Active.Count,
                snapshot.Current.SignIn.SelfRevoke,
                ReturnUrl = returnUrl,
            });
        }

        var app = (await apps.IdentityAsync(request.ClientId!, http.RequestAborted))!;
        var authorizationId = await devices.AppAuthorizationAsync(device.Id, app.Key);
        if (authorizationId is null || await authorizations.FindByIdAsync(authorizationId) is not { } authorization
            || !await authorizations.HasStatusAsync(authorization, Statuses.Valid))
        {
            var inbound = await db.Inbounds.AsNoTracking().Where(saved => saved.Slug == slug).Select(saved => saved.Name).SingleAsync();
            return ConnectResults.Page<ConsentPage>(new
            {
                AppName = app.Name,
                InboundName = inbound,
                app.RedirectHosts,
                app.LoopbackOnly,
                ClientName = client.Name,
                ReturnUrl = returnUrl,
                Request = Forwarded(request),
            });
        }

        await snapshot.RefreshAsync();
        var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, client.Id.ToString())
            .SetClaim(Claims.Name, client.Name)
            .SetClaim(ConnectClaims.Device, device.Id.ToString())
            .SetScopes(GrantedScopes)
            .SetResources(resources.Resource(slug))
            .SetAudiences(resources.Resource(slug))
            .SetAuthorizationId(authorizationId)
            .SetDestinations(_ => TokenEndpoint.Destinations);
        var principal = new ClaimsPrincipal(identity);
        principal.SetRefreshTokenLifetime(TimeSpan.FromDays(snapshot.Current.SignIn.IdleDays));
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> ConsentAsync(
        HttpContext http,
        [FromForm] string? returnUrl,
        ISnapshotSource snapshot,
        DeviceService devices,
        RegisteredApps apps,
        IOpenIddictAuthorizationManager authorizations,
        TimeProvider clock)
    {
        var target = AuthorizeTarget(returnUrl);
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, target);

        var clientId = QueryHelpers.ParseQuery(new Uri("http://local" + target).Query)[Parameters.ClientId].ToString();
        var admission = await devices.AdmitAsync(signedIn.Client.Id, DeviceMarkerCookie.GetOrIssue(http), signedIn.Seen(http));
        if (admission.Device is { } device
            && await apps.IdentityAsync(clientId, http.RequestAborted) is { } app
            && await apps.ApplicationIdAsync(clientId, http.RequestAborted) is { } applicationId)
        {
            var authorization = await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
            {
                ApplicationId = applicationId,
                Subject = signedIn.Client.Id.ToString(),
                Type = AuthorizationTypes.Permanent,
                Status = Statuses.Valid,
                CreationDate = clock.GetUtcNow(),
                Scopes = { ConnectServices.McpScope, Scopes.OfflineAccess },
            });
            await devices.AddAppAsync(device.Id, app.Key, app.Name, (await authorizations.GetIdAsync(authorization))!);
        }

        return ConnectResults.Back(http, target);
    }

    private static async Task<IResult> ReplaceAsync(
        HttpContext http,
        [FromForm] Guid deviceId,
        [FromForm] string? returnUrl,
        ISnapshotSource snapshot,
        DeviceService devices)
    {
        var target = AuthorizeTarget(returnUrl);
        if (!snapshot.Current.SignIn.SelfRevoke)
            return Results.NotFound();
        if (await SignedIn.ReadAsync(http, snapshot) is not { } signedIn)
            return ConnectResults.ToSignIn(http, target);

        await devices.ReplaceAsync(signedIn.Client.Id, deviceId, DeviceMarkerCookie.GetOrIssue(http), signedIn.Seen(http));
        await snapshot.RefreshAsync();
        return ConnectResults.Back(http, target);
    }

    private static string AuthorizeTarget(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith(Path + "?", StringComparison.Ordinal) ? returnUrl : ConnectResults.SignInPath;
}
