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
    UsageJournal journal,
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
            return Refused(UsageOutcome.RateLimited);
        }

        if (!HttpMethods.IsPost(context.Request.Method))
            return Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);

        using var slot = limiter.AcquireSlot(client);
        if (!slot.IsAcquired)
        {
            await Rejection.WriteBusyAsync(context);
            return Refused(UsageOutcome.ConcurrencyLimited);
        }

        var started = clock.GetTimestamp();
        var body = await RequestBody.ReadAsync(context.Request, route.MaxBodyBytes, context.RequestAborted);
        if (body.Status == BodyStatus.Aborted)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return UsageOutcome.ClientAborted;
        }

        if (body.Status == BodyStatus.TooLarge)
        {
            await Rejection.WriteBodyTooLargeAsync(context, route.MaxBodyBytes);
            return Refused(UsageOutcome.BodyTooLarge);
        }

        if (RpcEnvelope.Read(body.Bytes) is not { } envelope)
        {
            await Rejection.WriteNotJsonRpcAsync(context);
            return Refused(UsageOutcome.NotJsonRpc);
        }

        UsageOutcome outcome;
        var charge = envelope.ToolCalls > 0 ? ledger.Charge(client, envelope.ToolCalls, now) : (QuotaCharge?)null;
        if (charge is { Accepted: false } refused)
        {
            await Rejection.WriteQuotaExhaustedAsync(context, envelope, QuotaRefusal.Text(refused, now));
            outcome = UsageOutcome.QuotaExhausted;
        }
        else
        {
            context.Request.Body = new MemoryStream(body.Bytes, writable: false);
            outcome = Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);
            if (charge is { } accepted && outcome is UsageOutcome.UpstreamRejected or UsageOutcome.UpstreamFailed)
                ledger.Refund(client.Id, accepted);
        }

        var duration = (int)Math.Min(int.MaxValue, clock.GetElapsedTime(started).TotalMilliseconds);
        foreach (var call in envelope.Calls.Where(call => call.Method is not null))
            journal.Record(new UsageRecord(client.Id, route.Id, now, call.Method, call.Tool, duration, outcome));

        return outcome;

        UsageOutcome Refused(UsageOutcome reason)
        {
            journal.Record(new UsageRecord(client.Id, route.Id, now, null, null, 0, reason));
            return reason;
        }
    }
}
