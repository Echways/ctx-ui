using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;

namespace CtxUi.Core.ApiKeys;

public sealed record IssuedApiKey(ApiKey Key, string Secret);

public sealed class ApiKeyService(CtxUiDbContext db, TimeProvider clock)
{
    public async Task<IssuedApiKey> AddAsync(string name, CancellationToken ct = default)
    {
        var parsed = ApiKeyName.Parse(name);
        var key = ApiKeyName.Key(parsed);
        if (await db.ApiKeys.AnyAsync(known => known.NameKey == key, ct))
            throw new DomainException($"ключ «{parsed}» уже есть");

        var secret = ApiKeySecret.Generate();
        var apiKey = new ApiKey
        {
            Id = Guid.NewGuid(),
            Name = parsed,
            NameKey = key,
            SecretHash = ApiKeySecret.Hash(secret),
            SecretPrefix = ApiKeySecret.DisplayPrefix(secret),
            CreatedAt = clock.GetUtcNow(),
        };
        db.ApiKeys.Add(apiKey);
        await db.SaveChangesAsync(ct);
        return new IssuedApiKey(apiKey, secret);
    }

    public Task<List<ApiKey>> ListAsync(CancellationToken ct = default) =>
        db.ApiKeys.AsNoTracking().OrderBy(apiKey => apiKey.NameKey).ToListAsync(ct);

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        var key = ApiKeyName.Key(name.Trim());
        var apiKey = await db.ApiKeys.SingleOrDefaultAsync(known => known.NameKey == key, ct)
            ?? throw new NotFoundException($"ключ «{name.Trim()}» не найден");
        db.ApiKeys.Remove(apiKey);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ApiKey?> FindAsync(string? secret, CancellationToken ct = default)
    {
        if (!ApiKeySecret.IsWellFormed(secret))
            return null;

        var hash = ApiKeySecret.Hash(secret);
        return await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(apiKey => apiKey.SecretHash == hash, ct);
    }
}
