using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.SignIn;

public static class ConnectSession
{
    public const string Scheme = OwnCookies.ConnectSession;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static AuthenticationBuilder AddConnectSession(this AuthenticationBuilder auth) => auth.AddCookie(Scheme, options =>
    {
        options.Cookie.Name = OwnCookies.ConnectSession;
        options.Cookie.Path = ConnectServices.Prefix;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.IsEssential = true;
        options.ExpireTimeSpan = Lifetime;
        options.SlidingExpiration = false;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
        };
    });

    public static Task SignInAsync(HttpContext http, Guid clientId, string signedInWith)
    {
        var identity = new ClaimsIdentity(
            [new Claim(Claims.Subject, clientId.ToString()), new Claim(ConnectClaims.SignedInWith, signedInWith)],
            Scheme);
        return http.SignInAsync(Scheme, new ClaimsPrincipal(identity));
    }

    public static async Task<(Guid ClientId, string SignedInWith)?> ReadAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(Scheme);
        return result.Principal is { } principal
               && Guid.TryParse(principal.FindFirstValue(Claims.Subject), out var clientId)
               && principal.FindFirstValue(ConnectClaims.SignedInWith) is { } signedInWith
            ? (clientId, signedInWith)
            : null;
    }

    public static Task SignOutAsync(HttpContext http) => http.SignOutAsync(Scheme);
}
