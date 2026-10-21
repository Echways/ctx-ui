using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Clients;

public sealed record IssuedClient(Client Client, string Token);

public sealed record StoredUsage(Guid ClientId, DateTimeOffset Since, long Calls);

public sealed record ClientUpdate
{
    public bool ChangeExpiry { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    public IReadOnlyCollection<string>? InboundSlugs { get; init; }

    public ClientLimits? Limits { get; init; }

    public IEnumerable<KeyValuePair<string, string>>? Metadata { get; init; }
}

public sealed class ClientService(CtxUiDbContext db, ISecretVault vault, TimeProvider clock)
{
    public async Task<IssuedClient> AddAsync(
        string name,
        IReadOnlyCollection<string> inboundSlugs,
        DateTimeOffset? expiresAt = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        ClientLimits? limits = null,
        CancellationToken ct = default)
    {
        var parsedName = ClientName.Parse(name);
        var key = ClientName.Key(parsedName);
        var now = clock.GetUtcNow();
        EnsureFuture(expiresAt, now);
        var parsedMetadata = ClientMetadata.Parse(metadata);
        var parsedLimits = (limits ?? ClientLimits.None).Validated();
        if (await db.Clients.AnyAsync(client => client.NameKey == key, ct))
            throw new DomainException(string.Format(CoreText.ClientExists, parsedName));
        var inbounds = await ResolveInboundsAsync(inboundSlugs, ct);

        var token = AccessToken.Generate();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = parsedName,
            NameKey = key,
            TokenHash = AccessToken.Hash(token),
            TokenSealed = vault.Seal(token),
            TokenPrefix = AccessToken.DisplayPrefix(token),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            RatePerMinute = parsedLimits.RatePerMinute,
            MaxConcurrency = parsedLimits.MaxConcurrency,
            QuotaCalls = parsedLimits.QuotaCalls,
            ResetEveryDays = parsedLimits.ResetEveryDays,
            PeriodStartedAt = now,
            Metadata = parsedMetadata,
            Inbounds = inbounds.Select(inbound => new ClientInbound { InboundId = inbound.Id }).ToList(),
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync(ct);
        return new IssuedClient(client, token);
    }

    public Task<List<Client>> ListAsync(CancellationToken ct = default) =>
        db.Clients.AsNoTracking()
            .Include(client => client.Inbounds).ThenInclude(link => link.Inbound)
            .OrderBy(client => client.CreatedAt).ThenBy(client => client.NameKey)
            .ToListAsync(ct);

    public async Task<Client> GetAsync(string name, CancellationToken ct = default)
    {
        var key = ClientName.Key(name.Trim());
        return await db.Clients
                .Include(client => client.Inbounds).ThenInclude(link => link.Inbound)
                .SingleOrDefaultAsync(client => client.NameKey == key, ct)
            ?? throw new NotFoundException(string.Format(CoreText.ClientNotFound, name.Trim()));
    }

    public async Task<Client> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Clients.AsNoTracking()
            .Include(client => client.Inbounds).ThenInclude(link => link.Inbound)
            .SingleOrDefaultAsync(client => client.Id == id, ct)
        ?? throw new NotFoundException(CoreText.ClientIdNotFound);

    public async Task UpdateAsync(string name, ClientUpdate update, CancellationToken ct = default)
    {
        var client = await GetAsync(name, ct);
        if (update.ChangeExpiry)
        {
            EnsureFuture(update.ExpiresAt, clock.GetUtcNow());
            client.ExpiresAt = update.ExpiresAt;
        }

        if (update.InboundSlugs is not null)
        {
            var wanted = (await ResolveInboundsAsync(update.InboundSlugs, ct)).Select(inbound => inbound.Id).ToHashSet();
            client.Inbounds.RemoveAll(link => !wanted.Contains(link.InboundId));
            foreach (var inboundId in wanted.Except(client.Inbounds.Select(link => link.InboundId)).ToList())
                client.Inbounds.Add(new ClientInbound { ClientId = client.Id, InboundId = inboundId });
        }

        if (update.Metadata is not null)
            client.Metadata = ClientMetadata.Parse(update.Metadata);

        if (update.Limits is { } limits)
        {
            limits.Validated();
            if (limits.ResetEveryDays != client.ResetEveryDays || (client.QuotaCalls is null && limits.QuotaCalls is not null))
                client.PeriodStartedAt = clock.GetUtcNow();

            client.RatePerMinute = limits.RatePerMinute;
            client.MaxConcurrency = limits.MaxConcurrency;
            client.QuotaCalls = limits.QuotaCalls;
            client.ResetEveryDays = limits.ResetEveryDays;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task ResetQuotaAsync(string name, CancellationToken ct = default)
    {
        var client = await GetAsync(name, ct);
        client.PeriodStartedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        var client = await GetAsync(name, ct);
        client.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        db.Clients.Remove(await GetAsync(name, ct));
        await db.SaveChangesAsync(ct);
    }

    public async Task<string> ReissueAsync(string name, CancellationToken ct = default)
    {
        var client = await GetAsync(name, ct);
        var token = AccessToken.Generate();
        client.TokenHash = AccessToken.Hash(token);
        client.TokenSealed = vault.Seal(token);
        client.TokenPrefix = AccessToken.DisplayPrefix(token);
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<string?> RevealAsync(string name, CancellationToken ct = default) =>
        vault.Open((await GetAsync(name, ct)).TokenSealed);

    public async Task MarkUsedAsync(IReadOnlyCollection<KeyValuePair<Guid, DateTimeOffset>> seen, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var (clientId, at) in seen)
        {
            await db.Clients
                .Where(client => client.Id == clientId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(client => client.LastUsedAt, at)
                        .SetProperty(client => client.FirstUsedAt, client => client.FirstUsedAt ?? at),
                    ct);
        }

        await transaction.CommitAsync(ct);
    }

    public async Task<List<StoredUsage>> LoadUsageAsync(CancellationToken ct = default)
    {
        var counted = await db.Clients.AsNoTracking()
            .Where(client => client.UsedSince != null)
            .Select(client => new { client.Id, client.UsedSince, client.UsedCalls })
            .ToListAsync(ct);
        return counted.Select(row => new StoredUsage(row.Id, row.UsedSince!.Value, row.UsedCalls)).ToList();
    }

    public async Task SaveUsageAsync(IReadOnlyCollection<StoredUsage> usage, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var item in usage)
        {
            DateTimeOffset? since = item.Since;
            await db.Clients
                .Where(client => client.Id == item.ClientId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(client => client.UsedCalls, item.Calls)
                        .SetProperty(client => client.UsedSince, since),
                    ct);
        }

        await transaction.CommitAsync(ct);
    }

    private static void EnsureFuture(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is { } value && value <= now)
            throw new DomainException(string.Format(CoreText.ExpiryPassed, value));
    }

    private async Task<List<Inbound>> ResolveInboundsAsync(IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        var wanted = slugs.Select(slug => slug.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            throw new DomainException(CoreText.InboundRequired);

        var found = await db.Inbounds.Where(inbound => wanted.Contains(inbound.Slug)).ToListAsync(ct);
        var missing = wanted.Except(found.Select(inbound => inbound.Slug)).ToList();
        if (missing.Count > 0)
            throw new DomainException(string.Format(CoreText.InboundsMissing, string.Join(", ", missing)));

        return found;
    }
}
