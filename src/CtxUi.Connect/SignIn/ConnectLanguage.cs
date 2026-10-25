using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using CtxUi.Core;

namespace CtxUi.Connect.SignIn;

public static class ConnectLanguage
{
    private static readonly TimeSpan Remembered = TimeSpan.FromDays(365);

    public static string SwitchPath(HttpContext http) =>
        http.Request.PathBase + ConnectServices.Prefix + "/language/" + ConnectText.LanguageSwitchCulture
        + QueryString.Create("returnUrl", http.Request.Path + http.Request.QueryString);

    public static IResult Choose(HttpContext http, string culture, string? returnUrl)
    {
        if (!Languages.IsSupported(culture))
            return Results.NotFound();

        http.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions
            {
                Path = http.Request.PathBase + ConnectServices.Prefix,
                MaxAge = Remembered,
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
            });
        return Results.LocalRedirect(http.Request.PathBase + LocalPath(returnUrl, ConnectServices.Prefix + "/signin"));
    }

    public static string LocalPath(string? returnUrl, string fallback) =>
        returnUrl is not null
        && returnUrl.StartsWith(ConnectServices.Prefix + "/", StringComparison.Ordinal)
        && !returnUrl.Contains('\\')
        && !returnUrl.Contains("//", StringComparison.Ordinal)
            ? returnUrl
            : fallback;
}
