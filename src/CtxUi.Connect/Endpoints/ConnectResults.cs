using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using OpenIddict.Server.AspNetCore;
using CtxUi.Connect.Pages;

namespace CtxUi.Connect.Endpoints;

public static class ConnectResults
{
    public const string SignInPath = ConnectServices.Prefix + "/signin";
    public const string DevicesPath = ConnectServices.Prefix + "/devices";

    public static RazorComponentResult<TPage> Page<TPage>(object parameters, int status = StatusCodes.Status200OK)
        where TPage : IComponent =>
        new(parameters) { StatusCode = status };

    public static RazorComponentResult<NoticePage> Notice(string text, int status = StatusCodes.Status200OK) =>
        Page<NoticePage>(new { Text = text }, status);

    public static IResult ToSignIn(HttpContext http, string returnUrl) =>
        Results.Redirect(http.Request.PathBase + SignInPath + QueryString.Create("returnUrl", returnUrl));

    public static IResult Back(HttpContext http, string path) => Results.Redirect(http.Request.PathBase + path);

    public static IResult Refuse(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
