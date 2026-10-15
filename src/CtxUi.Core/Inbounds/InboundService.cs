using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Inbounds;

public sealed record InboundSettings
{
    public string? Name { get; init; }

    public required string Url { get; init; }

    public bool ForwardIdentity { get; init; }

    public int RequestTimeoutSeconds { get; init; } = Inbound.DefaultRequestTimeoutSeconds;

    public int MaxBodyBytes { get; init; } = Inbound.DefaultMaxBodyBytes;

    public IEnumerable<KeyValuePair<string, string>>? UpstreamHeaders { get; init; }
}

public sealed class InboundService(CtxUiDbContext db, ISecretVault vault, TimeProvider clock)
{
    public const int MaxNameLength = 100;

    public Task<Inbound> AddHttpAsync(
        string slug,
        string? name,
        string url,
        IEnumerable<KeyValuePair<string, string>>? upstreamHeaders = null,
        bool forwardIdentity = false,
        CancellationToken ct = default) =>
        AddHttpAsync(
            slug,
            new InboundSettings { Name = name, Url = url, UpstreamHeaders = upstreamHeaders, ForwardIdentity = forwardIdentity },
            ct);

    public async Task<Inbound> AddHttpAsync(string slug, InboundSettings settings, CancellationToken ct = default)
    {
        var parsedSlug = Slug.Parse(slug);
        var inbound = new Inbound
        {
            Id = Guid.NewGuid(),
            Slug = parsedSlug,
            Name = parsedSlug,
            Kind = InboundKind.Http,
            CreatedAt = clock.GetUtcNow(),
        };
        Apply(inbound, settings);
        if (await db.Inbounds.AnyAsync(known => known.Slug == parsedSlug, ct))
            throw new DomainException($"inbound «{parsedSlug}» уже есть");

        db.Inbounds.Add(inbound);
        await db.SaveChangesAsync(ct);
        return inbound;
    }

    public Task<List<Inbound>> ListAsync(CancellationToken ct = default) =>
        db.Inbounds.AsNoTracking().OrderBy(inbound => inbound.Slug).ToListAsync(ct);

    public Task<Inbound?> FindAsync(string slug, CancellationToken ct = default)
    {
        var wanted = slug.Trim();
        return db.Inbounds.SingleOrDefaultAsync(inbound => inbound.Slug == wanted, ct);
    }

    public async Task<Inbound> GetAsync(string slug, CancellationToken ct = default) =>
        await FindAsync(slug, ct) ?? throw new NotFoundException($"inbound «{slug}» не найден");

    public async Task UpdateAsync(string slug, InboundSettings settings, CancellationToken ct = default)
    {
        Apply(await GetAsync(slug, ct), settings);
        await db.SaveChangesAsync(ct);
    }

    public IReadOnlyList<string>? HeaderNames(Inbound inbound) =>
        UpstreamHeaders.Open(vault, inbound.UpstreamHeadersSealed)?.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public Task<Dictionary<Guid, int>> ClientCountsAsync(CancellationToken ct = default) =>
        db.ClientInbounds.AsNoTracking()
            .GroupBy(link => link.InboundId)
            .Select(group => new { InboundId = group.Key, Clients = group.Count() })
            .ToDictionaryAsync(row => row.InboundId, row => row.Clients, ct);

    public async Task SetEnabledAsync(string slug, bool enabled, CancellationToken ct = default)
    {
        var inbound = await GetAsync(slug, ct);
        inbound.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        db.Inbounds.Remove(await GetAsync(slug, ct));
        await db.SaveChangesAsync(ct);
    }

    private void Apply(Inbound inbound, InboundSettings settings)
    {
        var name = ParseName(settings.Name, inbound.Slug);
        var url = ParseUrl(settings.Url);
        if (settings.RequestTimeoutSeconds is < 1 or > Inbound.RequestTimeoutCeiling)
            throw new DomainException($"таймаут запроса: допустимо целое от 1 до {Inbound.RequestTimeoutCeiling} секунд");
        if (settings.MaxBodyBytes is < Inbound.MaxBodyBytesFloor or > Inbound.MaxBodyBytesCeiling)
            throw new DomainException(
                $"предел тела запроса: допустимо от {Inbound.MaxBodyBytesFloor} до {Inbound.MaxBodyBytesCeiling} байт");
        var headers = settings.UpstreamHeaders is null ? null : UpstreamHeaders.Parse(settings.UpstreamHeaders);

        inbound.Name = name;
        inbound.Url = url.AbsoluteUri;
        inbound.ForwardIdentity = settings.ForwardIdentity;
        inbound.RequestTimeoutSeconds = settings.RequestTimeoutSeconds;
        inbound.MaxBodyBytes = settings.MaxBodyBytes;
        if (headers is not null)
            inbound.UpstreamHeadersSealed = UpstreamHeaders.Seal(vault, headers);
    }

    private static string ParseName(string? name, string fallback)
    {
        var parsed = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        if (parsed.Length > MaxNameLength || parsed.Any(char.IsControl))
            throw new DomainException($"название inbound'а должно быть одной строкой до {MaxNameLength} символов");

        return parsed;
    }

    private static Uri ParseUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            throw new DomainException($"адрес «{value}» недопустим: нужен полный адрес, начинающийся с http:// или https://");
        if (url.UserInfo.Length > 0 || url.Fragment.Length > 0)
            throw new DomainException("в адресе не должно быть логина, пароля и #фрагмента; секрет передавайте заголовком");

        return url;
    }
}
