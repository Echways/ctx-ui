using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Connect.Endpoints;

public static class PageForms
{
    public static RouteHandlerBuilder RequireSetting(this RouteHandlerBuilder endpoint, Func<SignInConfig, bool> isOn) =>
        endpoint.AddEndpointFilter(async (context, next) =>
            isOn(context.HttpContext.RequestServices.GetRequiredService<ISnapshotSource>().Current.SignIn)
                ? await next(context)
                : Results.NotFound());

    public static RouteHandlerBuilder RequirePageForm(this RouteHandlerBuilder endpoint) =>
        endpoint.DisableAntiforgery().AddEndpointFilter(async (context, next) =>
            await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(context.HttpContext)
                ? await next(context)
                : ConnectResults.Notice(ConnectText.PageExpired, StatusCodes.Status400BadRequest, ConnectText.PageExpiredTitle));
}
