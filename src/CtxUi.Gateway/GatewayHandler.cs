using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed partial class GatewayHandler(
    SnapshotStore store,
    CredentialReader reader,
    IOptions<GatewayOptions> options,
    HttpUpstream upstream,
    ClientTouch touch,
    DeviceTouch deviceTouch,
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
        var snapshot = store.Current;
        var publicUrl = options.Value.PublicUrl;
        var active = snapshot.SignIn.IsActive(publicUrl);
        var credential = await reader.ReadAsync(context, OAuthResource.For(publicUrl, slug), active, context.RequestAborted);
        var decision = snapshot.Authorize(credential, slug, now);

        object outcome = decision.Status;
        if (decision.Status == AccessStatus.Allowed)
            outcome = await ServeAsync(context, decision, now);
        else
            await Rejection.WriteAsync(context, decision, active ? OAuthResource.MetadataUrl(publicUrl, slug) : null);

        Served(
            logger,
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

    private async Task<UsageOutcome> ServeAsync(HttpContext context, AccessDecision decision, DateTimeOffset now)
    {
        var (route, client, deviceId) = (decision.Route!, decision.Client!, decision.DeviceId);
        touch.Touch(client.Id, now);
        if (deviceId is { } device)
            deviceTouch.Touch(device, now);

        using var rate = limiter.AcquireRate(client);
        if (!rate.IsAcquired)
        {
            await Rejection.WriteRateLimitedAsync(context, ClientLimiter.SecondsUntilNextRequest(client));
            return Refused(UsageOutcome.RateLimited);
        }

        if (!HttpMethods.IsPost(context.Request.Method))
            return Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);

        using var slot = await limiter.AcquireSlotAsync(client, context.RequestAborted);
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
            context.Request.Body = new MemoryStream(body.Bytes.Array!, body.Bytes.Offset, body.Bytes.Count, writable: false);
            outcome = Outcome(await upstream.ForwardAsync(context, route, client), context.Response.StatusCode);
            if (charge is { } accepted && outcome is UsageOutcome.UpstreamRejected or UsageOutcome.UpstreamFailed)
                ledger.Refund(client.Id, accepted);
        }

        var duration = (int)Math.Min(int.MaxValue, clock.GetElapsedTime(started).TotalMilliseconds);
        foreach (var call in envelope.Calls.Where(call => call.Method is not null))
            journal.Record(new UsageRecord(client.Id, route.Id, now, call.Method, call.Tool, duration, outcome, deviceId));

        return outcome;

        UsageOutcome Refused(UsageOutcome reason)
        {
            journal.Record(new UsageRecord(client.Id, route.Id, now, null, null, 0, reason, deviceId));
            return reason;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Method} /mcp/{Slug} client={Client} outcome={Outcome} status={StatusCode}")]
    private static partial void Served(ILogger logger, string method, string slug, string client, object outcome, int statusCode);
}
