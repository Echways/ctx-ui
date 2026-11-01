using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.ApiKeys;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Usage;
using CtxUi.Extensions;

namespace CtxUi.Panel;

public sealed class PanelData(PanelBackend backend) : IPanelData
{
    public static ClientSummary Summary(Client client) => new(
        client.Id,
        client.Name,
        client.Enabled,
        client.CreatedAt,
        client.ExpiresAt,
        client.LastUsedAt,
        client.UsedCalls,
        client.QuotaCalls,
        client.Metadata);

    public static InboundSummary Summary(Inbound inbound) => new(inbound.Id, inbound.Slug, inbound.Name, inbound.Enabled, inbound.CreatedAt);

    public static ApiKeySummary Summary(ApiKey key) => new(key.Id, key.Name);

    public Task<IReadOnlyList<ClientSummary>> ClientsAsync(CancellationToken ct = default) =>
        backend.ReadAsync<IReadOnlyList<ClientSummary>>(async services =>
            [.. (await services.GetRequiredService<ClientService>().ListAsync(ct)).Select(Summary)]);

    public Task<ClientSummary> ClientAsync(Guid id, CancellationToken ct = default) =>
        backend.ReadAsync(async services => Summary(await services.GetRequiredService<ClientService>().GetAsync(id, ct)));

    public Task SetClientEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        backend.ChangeAsync(services => services.GetRequiredService<ClientService>().SetEnabledAsync(id, enabled, ct));

    public Task ResetClientQuotaAsync(Guid id, CancellationToken ct = default) =>
        backend.ChangeAsync(services => services.GetRequiredService<ClientService>().ResetQuotaAsync(id, ct));

    public Task<IReadOnlyList<InboundSummary>> InboundsAsync(CancellationToken ct = default) =>
        backend.ReadAsync<IReadOnlyList<InboundSummary>>(async services =>
            [.. (await services.GetRequiredService<InboundService>().ListAsync(ct)).Select(Summary)]);

    public Task SetInboundEnabledAsync(string slug, bool enabled, CancellationToken ct = default) =>
        backend.ChangeAsync(services => services.GetRequiredService<InboundService>().SetEnabledAsync(slug, enabled, ct));

    public Task<InboundSummary> AddHttpInboundAsync(HttpInboundDraft draft, CancellationToken ct = default) =>
        backend.ChangeAsync(async services => Summary(await services.GetRequiredService<InboundService>().AddHttpAsync(
            draft.Slug,
            new InboundSettings
            {
                Name = draft.Name,
                Url = draft.Url,
                UpstreamHeaders = draft.UpstreamHeaders,
                ForwardIdentity = draft.ForwardIdentity,
            },
            ct)));

    public Task RemoveInboundAsync(string slug, CancellationToken ct = default) =>
        backend.ChangeAsync(services => services.GetRequiredService<InboundService>().RemoveAsync(slug, ct));

    public Task<IReadOnlyList<Guid>> InboundClientsAsync(string slug, CancellationToken ct = default) =>
        backend.ReadAsync(services => services.GetRequiredService<ClientService>().InboundClientsAsync(slug, ct));

    public Task SetInboundClientsAsync(string slug, IReadOnlyCollection<Guid> clientIds, CancellationToken ct = default) =>
        backend.ChangeAsync(services => services.GetRequiredService<ClientService>().SetInboundClientsAsync(slug, clientIds, ct));

    public Task<IReadOnlyList<UsageDay>> UsageDaysAsync(DateOnly from, DateOnly to, Guid? clientId = null, CancellationToken ct = default) =>
        backend.ReadAsync<IReadOnlyList<UsageDay>>(async services =>
            [.. (await services.GetRequiredService<UsageStore>().DaysAsync(from, to, clientId, ct))
                .Select(daily => new UsageDay(daily.Day, daily.ClientId, daily.InboundId, daily.Calls, daily.ToolCalls, daily.Errors, daily.Refused, daily.DurationMs))]);
}
