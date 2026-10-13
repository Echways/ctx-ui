using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Routing;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed class GatewayHandler(
    SnapshotStore store,
    HttpUpstream upstream,
    ClientTouch touch,
    ClientLimiter limiter,
    TimeProvider clock,
    ILogger<GatewayHandler> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        var slug = (string)context.Request.RouteValues["slug"]!;
        var decision = store.Current.Authorize(ClientCredentials.Read(context), slug, clock.GetUtcNow());

        object outcome = decision.Status;
        if (decision.Status == AccessStatus.Allowed)
            outcome = await ServeAsync(context, decision.Route!, decision.Client!);
        else
            await Rejection.WriteAsync(context, decision);

        logger.LogInformation(
            "{Method} /mcp/{Slug} клиент={Client} исход={Outcome} статус={StatusCode}",
            context.Request.Method,
            decision.Route?.Slug ?? "?",
            decision.Client?.Name ?? "-",
            outcome,
            context.Response.StatusCode);
    }

    private async Task<UsageOutcome> ServeAsync(HttpContext context, InboundRoute route, ClientAccess client)
    {
        await touch.TouchAsync(client.Id);

        using var rate = limiter.AcquireRate(client);
        if (!rate.IsAcquired)
        {
            await Rejection.WriteRateLimitedAsync(context, ClientLimiter.SecondsUntilNextRequest(client));
            return UsageOutcome.RateLimited;
        }

        using var slot = HttpMethods.IsPost(context.Request.Method) ? limiter.AcquireSlot(client) : null;
        if (slot is { IsAcquired: false })
        {
            await Rejection.WriteBusyAsync(context);
            return UsageOutcome.ConcurrencyLimited;
        }

        await upstream.ForwardAsync(context, route, client);
        return UsageOutcome.Ok;
    }
}
