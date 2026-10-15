using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace CtxUi.Panel;

internal static class PanelLanguage
{
    public const string Default = "en";

    public static readonly string[] Cultures = [Default, "ru"];

    private static readonly TimeSpan Remembered = TimeSpan.FromDays(365);

    public static string SwitchPath(NavigationManager navigation) =>
        $"language/{PanelText.LanguageSwitchCulture}?back={Uri.EscapeDataString(navigation.ToBaseRelativePath(navigation.Uri))}";

    public static IResult Choose(HttpContext context, string culture, string? back)
    {
        if (!Cultures.Contains(culture, StringComparer.Ordinal))
            return Results.NotFound();

        var home = context.Request.PathBase + "/";
        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Path = home, MaxAge = Remembered, HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax });
        return Results.LocalRedirect(home + back?.TrimStart('/', '\\'));
    }
}
