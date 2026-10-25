using Microsoft.AspNetCore.Http;
using CtxUi.Core;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.SignIn;

public static class DeviceMarkerCookie
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    public static string GetOrIssue(HttpContext http)
    {
        if (http.Request.Cookies[OwnCookies.DeviceMarker] is { } known && DeviceMarker.IsWellFormed(known))
            return known;

        var marker = DeviceMarker.Generate();
        http.Response.Cookies.Append(OwnCookies.DeviceMarker, marker, new CookieOptions
        {
            Path = ConnectServices.Prefix,
            MaxAge = Lifetime,
            HttpOnly = true,
            Secure = http.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
        });
        return marker;
    }
}
