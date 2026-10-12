using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

public sealed class GatewayHandler(
    SnapshotStore store,
    HttpUpstream upstream,
    ClientTouch touch,
    TimeProvider clock,
    ILogger<GatewayHandler> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        var slug = (string)context.Request.RouteValues["slug"]!;
        var decision = store.Current.Authorize(ClientCredentials.Read(context), slug, clock.GetUtcNow());

        if (decision.Status == AccessStatus.Allowed)
        {
            await touch.TouchAsync(decision.Client!.Id);
            await upstream.ForwardAsync(context, decision.Route!, decision.Client);
        }
        else
        {
            await Rejection.WriteAsync(context, decision);
        }

        logger.LogInformation(
            "{Method} /mcp/{Slug} клиент={Client} исход={Outcome} статус={StatusCode}",
            context.Request.Method,
            decision.Route?.Slug ?? "?",
            decision.Client?.Name ?? "-",
            decision.Status,
            context.Response.StatusCode);
    }
}
