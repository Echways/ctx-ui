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
    QuotaLedger ledger,
    TimeProvider clock,
    ILogger<GatewayHandler> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        var slug = (string)context.Request.RouteValues["slug"]!;
        var now = clock.GetUtcNow();
        var decision = store.Current.Authorize(ClientCredentials.Read(context), slug, now);

        object outcome = decision.Status;
        if (decision.Status == AccessStatus.Allowed)
            outcome = await ServeAsync(context, decision.Route!, decision.Client!, now);
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

    private static UsageOutcome Outcome(ForwardOutcome forward, int status) => forward switch
    {
        ForwardOutcome.ClientAborted => UsageOutcome.ClientAborted,
        ForwardOutcome.UpstreamFailed => UsageOutcome.UpstreamFailed,
        _ when status >= StatusCodes.Status500InternalServerError => UsageOutcome.UpstreamFailed,
        _ when status >= StatusCodes.Status400BadRequest => UsageOutcome.UpstreamRejected,
        _ => UsageOutcome.Ok,
    };

    private async Task<UsageOutcome> ServeAsync(HttpContext context, InboundRoute route, ClientAccess client, DateTimeOffset now)
    {
        await touch.TouchAsync(client.Id);

        using var rate = limiter.AcquireRate(client);
        if (!rate.IsAcquired)
        {
            await Rejection.WriteRateLimitedAsync(context, ClientLimiter.SecondsUntilNextRequest(client));
            return UsageOutcome.RateLimited;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
            return Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);

        using var slot = limiter.AcquireSlot(client);
        if (!slot.IsAcquired)
        {
            await Rejection.WriteBusyAsync(context);
            return UsageOutcome.ConcurrencyLimited;
        }

        var body = await RequestBody.ReadAsync(context.Request, route.MaxBodyBytes, context.RequestAborted);
        if (body.Status == BodyStatus.Aborted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return UsageOutcome.ClientAborted;
        }

        if (body.Status == BodyStatus.TooLarge)
        {
            await Rejection.WriteBodyTooLargeAsync(context, route.MaxBodyBytes);
            return UsageOutcome.BodyTooLarge;
        }

        if (RpcEnvelope.Read(body.Bytes) is not { } envelope)
        {
            await Rejection.WriteNotJsonRpcAsync(context);
            return UsageOutcome.NotJsonRpc;
        }

        var charge = envelope.ToolCalls > 0 ? ledger.Charge(client, envelope.ToolCalls, now) : (QuotaCharge?)null;
        if (charge is { Accepted: false } refused)
        {
            await Rejection.WriteQuotaExhaustedAsync(context, envelope, QuotaRefusal.Text(refused, now));
            return UsageOutcome.QuotaExhausted;
        }

        context.Request.Body = new MemoryStream(body.Bytes, writable: false);
        var outcome = Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);
        if (charge is { } accepted && outcome is UsageOutcome.UpstreamRejected or UsageOutcome.UpstreamFailed)
            ledger.Refund(client.Id, accepted);

        return outcome;
    }
}
