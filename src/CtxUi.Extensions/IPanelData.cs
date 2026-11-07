namespace CtxUi.Extensions;

public interface IPanelData
{
    Task<IReadOnlyList<ClientSummary>> ClientsAsync(CancellationToken ct = default);

    Task<ClientSummary> ClientAsync(Guid id, CancellationToken ct = default);

    Task SetClientEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);

    Task ResetClientQuotaAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<InboundSummary>> InboundsAsync(CancellationToken ct = default);

    Task SetInboundEnabledAsync(string slug, bool enabled, CancellationToken ct = default);

    Task<InboundSummary> AddHttpInboundAsync(HttpInboundDraft draft, CancellationToken ct = default);

    Task RemoveInboundAsync(string slug, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> InboundClientsAsync(string slug, CancellationToken ct = default);

    Task SetInboundClientsAsync(string slug, IReadOnlyCollection<Guid> clientIds, CancellationToken ct = default);

    Task<IReadOnlyList<UsageDay>> UsageDaysAsync(DateOnly from, DateOnly to, Guid? clientId = null,
        CancellationToken ct = default);
}
