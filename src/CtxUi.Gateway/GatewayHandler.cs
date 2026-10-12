using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Routing;

namespace CtxUi.Gateway;

/// <summary>Конечная точка /mcp/{slug}: решение о доступе, затем пересылка или отказ.</summary>
public sealed class GatewayHandler(SnapshotStore store, HttpUpstream upstream, TimeProvider clock, ILogger<GatewayHandler> logger)
{
    public async Task HandleAsync(HttpContext context)
    {
        var slug = (string)context.Request.RouteValues["slug"]!;
        var decision = store.Current.Authorize(ClientCredentials.Read(context), slug, clock.GetUtcNow());

        if (decision.Status == AccessStatus.Allowed)
            await upstream.ForwardAsync(context, decision.Route!, decision.Client!);
        else
            await Rejection.WriteAsync(context, decision);

        // В журнал идёт slug только известного inbound'а: произвольная строка из адреса туда не попадает.
        logger.LogInformation(
            "{Method} /mcp/{Slug} клиент={Client} исход={Outcome} статус={StatusCode}",
            context.Request.Method,
            decision.Route?.Slug ?? "?",
            decision.Client?.Name ?? "-",
            decision.Status,
            context.Response.StatusCode);
    }
}
