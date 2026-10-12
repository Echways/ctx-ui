using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CtxUi.Gateway;

public static class TokenPath
{
    private const string Marker = "/k/";
    private const string ItemKey = "CtxUi.PathToken";

    public static bool TrySplit(string? path, out string token, out string rest)
    {
        token = "";
        rest = "";
        if (path is null || !path.StartsWith(Marker, StringComparison.Ordinal))
            return false;

        var end = path.IndexOf('/', Marker.Length);
        token = end < 0 ? path[Marker.Length..] : path[Marker.Length..end];
        rest = end < 0 ? "/" : path[end..];
        return true;
    }

    public static IApplicationBuilder UseTokenPath(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (TrySplit(context.Request.Path.Value, out var token, out var rest))
        {
            var path = new PathString(rest);
            context.Items[ItemKey] = token;
            context.Request.Path = path;
            context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget =
                path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        }

        await next(context);
    });

    public static string? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var token) ? (string?)token : null;
}
