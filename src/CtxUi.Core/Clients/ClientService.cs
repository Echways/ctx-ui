using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Clients;

/// <summary>Клиент вместе с открытым токеном. Открытый токен нигде, кроме этого значения, не хранится.</summary>
public sealed record IssuedClient(Client Client, string Token);

public sealed record ClientUpdate
{
    /// <summary>true, если срок меняется. Тогда ExpiresAt = null означает «бессрочно».</summary>
    public bool ChangeExpiry { get; init; }

    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Новый полный набор inbound'ов или null, если набор не меняется.</summary>
    public IReadOnlyCollection<string>? InboundSlugs { get; init; }
}

public sealed class ClientService(CtxUiDbContext db, ISecretVault vault, TimeProvider clock)
{
    public async Task<IssuedClient> AddAsync(
        string name,
        IReadOnlyCollection<string> inboundSlugs,
        DateTimeOffset? expiresAt = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        CancellationToken ct = default)
    {
        var parsedName = ClientName.Parse(name);
        var key = ClientName.Key(parsedName);
        var now = clock.GetUtcNow();
        EnsureFuture(expiresAt, now);
        var parsedMetadata = ClientMetadata.Parse(metadata);
        if (await db.Clients.AnyAsync(client => client.NameKey == key, ct))
            throw new DomainException($"клиент «{parsedName}» уже есть");
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
            ?? throw new DomainException($"клиент «{name.Trim()}» не найден");
    }

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
            // Правим набор разницей: удаление и повторное добавление той же связи
            // в одном контексте конфликтует по составному ключу.
            var wanted = (await ResolveInboundsAsync(update.InboundSlugs, ct)).Select(inbound => inbound.Id).ToHashSet();
            client.Inbounds.RemoveAll(link => !wanted.Contains(link.InboundId));
            foreach (var inboundId in wanted.Except(client.Inbounds.Select(link => link.InboundId)).ToList())
                client.Inbounds.Add(new ClientInbound { ClientId = client.Id, InboundId = inboundId });
        }

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

    /// <summary>
    /// Отмечает обращение клиента. Идёт мимо отслеживания изменений,
    /// поэтому не увеличивает ревизию маршрутов и не заставляет сервер перечитывать таблицу.
    /// </summary>
    public async Task MarkUsedAsync(Guid clientId, DateTimeOffset now, CancellationToken ct = default)
    {
        await db.Clients
            .Where(client => client.Id == clientId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(client => client.LastUsedAt, now), ct);
        await db.Clients
            .Where(client => client.Id == clientId && client.FirstUsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(client => client.FirstUsedAt, now), ct);
    }

    private static void EnsureFuture(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is { } value && value <= now)
            throw new DomainException($"срок {value:yyyy-MM-dd} уже прошёл");
    }

    private async Task<List<Inbound>> ResolveInboundsAsync(IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        var wanted = slugs.Select(slug => slug.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
            throw new DomainException("укажите хотя бы один inbound");

        var found = await db.Inbounds.Where(inbound => wanted.Contains(inbound.Slug)).ToListAsync(ct);
        var missing = wanted.Except(found.Select(inbound => inbound.Slug)).ToList();
        if (missing.Count > 0)
            throw new DomainException($"inbound не найден: {string.Join(", ", missing)}");

        return found;
    }
}
