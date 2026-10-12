using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.Inbounds;

public sealed class InboundService(CtxUiDbContext db, ISecretVault vault, TimeProvider clock)
{
    public const int MaxNameLength = 100;

    public async Task<Inbound> AddHttpAsync(
        string slug,
        string? name,
        string url,
        IEnumerable<KeyValuePair<string, string>>? upstreamHeaders = null,
        bool forwardIdentity = false,
        CancellationToken ct = default)
    {
        var parsedSlug = Slug.Parse(slug);
        var parsedName = ParseName(name, parsedSlug);
        var parsedUrl = ParseUrl(url);
        var headers = UpstreamHeaders.Parse(upstreamHeaders);
        if (await db.Inbounds.AnyAsync(inbound => inbound.Slug == parsedSlug, ct))
            throw new DomainException($"inbound «{parsedSlug}» уже есть");

        var inbound = new Inbound
        {
            Id = Guid.NewGuid(),
            Slug = parsedSlug,
            Name = parsedName,
            Kind = InboundKind.Http,
            Url = parsedUrl.AbsoluteUri,
            UpstreamHeadersSealed = UpstreamHeaders.Seal(vault, headers),
            ForwardIdentity = forwardIdentity,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Inbounds.Add(inbound);
        await db.SaveChangesAsync(ct);
        return inbound;
    }

    public Task<List<Inbound>> ListAsync(CancellationToken ct = default) =>
        db.Inbounds.AsNoTracking().OrderBy(inbound => inbound.Slug).ToListAsync(ct);

    public async Task SetEnabledAsync(string slug, bool enabled, CancellationToken ct = default)
    {
        var inbound = await FindAsync(slug, ct);
        inbound.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string slug, CancellationToken ct = default)
    {
        db.Inbounds.Remove(await FindAsync(slug, ct));
        await db.SaveChangesAsync(ct);
    }

    private async Task<Inbound> FindAsync(string slug, CancellationToken ct)
    {
        var wanted = slug.Trim();
        return await db.Inbounds.SingleOrDefaultAsync(inbound => inbound.Slug == wanted, ct)
            ?? throw new DomainException($"inbound «{slug}» не найден");
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
