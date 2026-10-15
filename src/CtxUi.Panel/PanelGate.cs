using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CtxUi.Panel;

public sealed class PanelGate
{
    private volatile string? _prefix;

    public void Open(string path) => _prefix = "/" + path;

    public async Task PassAsync(HttpContext context, RequestDelegate next, IReadOnlyCollection<PathString> openPaths)
    {
        var request = context.Request;
        if (_prefix is { } prefix && request.Path.StartsWithSegments(prefix, StringComparison.Ordinal, out var rest))
        {
            if (!rest.HasValue)
            {
                context.Response.Redirect(request.PathBase + prefix + "/" + request.QueryString);
                return;
            }

            request.PathBase = request.PathBase.Add(prefix);
            request.Path = rest;
            context.Features.GetRequiredFeature<IHttpRequestFeature>().RawTarget =
                rest.ToUriComponent() + request.QueryString.ToUriComponent();
            await next(context);
            return;
        }

        if (openPaths.Any(open => request.Path.StartsWithSegments(open, StringComparison.Ordinal)))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
