using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Routing;

public sealed partial class RoutingSnapshotLoader(CtxUiDbContext db, ISecretVault vault, ILogger<RoutingSnapshotLoader> logger)
{
    public Task<long> CurrentRevisionAsync(CancellationToken ct = default) =>
        db.RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync(ct);

    public async Task<RoutingSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var revision = await CurrentRevisionAsync(ct);

        var routes = new List<InboundRoute>();
        var inbounds = await db.Inbounds.AsNoTracking()
            .Where(inbound => inbound.Enabled && inbound.Kind == InboundKind.Http)
            .ToListAsync(ct);
        foreach (var inbound in inbounds)
        {
            var headers = UpstreamHeaders.Open(vault, inbound.UpstreamHeadersSealed);
            if (headers is null || inbound.Url is null)
            {
                InboundSkipped(logger, inbound.Slug);
                continue;
            }

            routes.Add(new InboundRoute(
                inbound.Id,
                inbound.Slug,
                new Uri(inbound.Url),
                headers,
                inbound.ForwardIdentity,
                TimeSpan.FromSeconds(inbound.RequestTimeoutSeconds))
            {
                MaxBodyBytes = inbound.MaxBodyBytes,
            });
        }

        var clients = await db.Clients.AsNoTracking().Include(client => client.Inbounds).ToListAsync(ct);
        return new RoutingSnapshot(revision, routes, clients.Select(client => KeyValuePair.Create(
            client.TokenHash,
            new ClientAccess(
                client.Id,
                client.Name,
                client.Enabled,
                client.ExpiresAt,
                client.Inbounds.Select(link => link.InboundId).ToFrozenSet(),
                client.Metadata)
            {
                Limits = ClientLimits.Of(client),
                PeriodStartedAt = client.PeriodStartedAt,
            })));
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "inbound {Slug} skipped: no address or upstream headers cannot be decrypted, check the keys directory")]
    private static partial void InboundSkipped(ILogger logger, string slug);
}
