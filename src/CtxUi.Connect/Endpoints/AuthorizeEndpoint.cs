using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using CtxUi.Connect.Resources;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Endpoints;

public static class AuthorizeEndpoint
{
    public const string Path = ConnectServices.Prefix + "/authorize";
    public const string SignInPath = ConnectServices.Prefix + "/signin";

    public static IEndpointRouteBuilder MapAuthorize(this IEndpointRouteBuilder app)
    {
        app.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], Authorize).DisableAntiforgery();
        return app;
    }

    public static string ReturnUrl(HttpContext http) =>
        http.Request.PathBase + Path + QueryString.Create(
            (http.GetOpenIddictServerRequest() ?? new OpenIddictRequest())
            .GetParameters()
            .Select(parameter => KeyValuePair.Create(parameter.Key, (string?)parameter.Value)));

    public static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static IResult Authorize(HttpContext http, ResourceHandlers resources)
    {
        var request = http.GetOpenIddictServerRequest()!;
        if (resources.SlugOf(request) is null)
            return Refuse(Errors.InvalidTarget, ResourceHandlers.Refusal);

        return Results.Redirect(SignInPath + QueryString.Create("returnUrl", ReturnUrl(http)));
    }
}
