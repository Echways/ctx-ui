using CtxUi.Extensions;
using Enfilade;

namespace CtxUi.Builder.Tests
{
    public sealed class FakePanelData : IPanelData
    {
        public List<ClientSummary> Clients { get; } = [];

        public List<InboundSummary> Inbounds { get; } = [];

        public List<HttpInboundDraft> Drafts { get; } = [];

        public Dictionary<string, HashSet<Guid>> Links { get; } = [];

        public Action<string>? OnRemove { get; set; }

        public Exception? AddFailure { get; set; }

        public Func<Task>? DuringAdd { get; set; }

        public ClientSummary AddClient(string name)
        {
            var client = new ClientSummary(Guid.NewGuid(), name, true, DateTimeOffset.UtcNow, null, null, 0, null, new Dictionary<string, string>());
            Clients.Add(client);
            return client;
        }

        public InboundSummary AddInbound(string slug)
        {
            var inbound = new InboundSummary(Guid.NewGuid(), slug, slug, true, DateTimeOffset.UtcNow);
            Inbounds.Add(inbound);
            Links[slug] = [];
            return inbound;
        }

        public Task<IReadOnlyList<ClientSummary>> ClientsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClientSummary>>([.. Clients]);

        public Task<ClientSummary> ClientAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Clients.Single(client => client.Id == id));

        public Task SetClientEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public Task ResetClientQuotaAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<InboundSummary>> InboundsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InboundSummary>>([.. Inbounds]);

        public Task SetInboundEnabledAsync(string slug, bool enabled, CancellationToken ct = default) => Task.CompletedTask;

        public async Task<InboundSummary> AddHttpInboundAsync(HttpInboundDraft draft, CancellationToken ct = default)
        {
            if (AddFailure is not null)
                throw AddFailure;
            if (Inbounds.Any(inbound => inbound.Slug == draft.Slug))
                throw new PanelException($"Inbound {draft.Slug} exists.");
            Drafts.Add(draft);
            var inbound = AddInbound(draft.Slug);
            if (DuringAdd is { } during)
            {
                DuringAdd = null;
                await during();
            }

            return inbound;
        }

        public Task RemoveInboundAsync(string slug, CancellationToken ct = default)
        {
            OnRemove?.Invoke(slug);
            Inbounds.RemoveAll(inbound => inbound.Slug == slug);
            Links.Remove(slug);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> InboundClientsAsync(string slug, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([.. Links.GetValueOrDefault(slug) ?? []]);

        public Task SetInboundClientsAsync(string slug, IReadOnlyCollection<Guid> clientIds, CancellationToken ct = default)
        {
            if (clientIds.Any(id => Clients.All(client => client.Id != id)))
                throw new PanelException("Client not found.");
            Links[slug] = [.. clientIds];
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UsageDay>> UsageDaysAsync(DateOnly from, DateOnly to, Guid? clientId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UsageDay>>([]);
    }
}
