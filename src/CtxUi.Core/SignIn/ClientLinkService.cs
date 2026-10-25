using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;

namespace CtxUi.Core.SignIn;

public enum LinkOutcome
{
    Linked,
    AlreadyLinked,
    InviteInvalid,
    TakenByOtherClient,
}

public sealed record LinkResult(LinkOutcome Outcome, Guid? ClientId);

public sealed class ClientLinkService(CtxUiDbContext db, TimeProvider clock)
{
    public async Task<string> InviteAsync(Guid clientId, CancellationToken ct = default)
    {
        var client = await db.Clients.SingleOrDefaultAsync(saved => saved.Id == clientId, ct)
            ?? throw new NotFoundException(CoreText.ClientIdNotFound);
        var code = InviteCode.Generate();
        client.InviteHash = InviteCode.Hash(code);
        client.InviteExpiresAt = clock.GetUtcNow() + InviteCode.Lifetime;
        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task<Guid?> InvitedClientAsync(string code, CancellationToken ct = default) =>
        (await FindInvitedAsync(code, ct))?.Id;

    public async Task<LinkResult> LinkAsync(string code, ExternalIdentity identity, CancellationToken ct = default)
    {
        var client = await FindInvitedAsync(code, ct);
        if (client is null)
            return new LinkResult(LinkOutcome.InviteInvalid, null);

        var owner = await ClientOfAsync(identity, ct);
        if (owner is { } other && other != client.Id)
            return new LinkResult(LinkOutcome.TakenByOtherClient, other);

        if (owner is null)
        {
            db.ClientLinks.Add(new ClientLink
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                Provider = identity.Provider,
                Subject = identity.Subject,
                Display = identity.Display,
                LinkedAt = clock.GetUtcNow(),
            });
        }

        client.InviteHash = null;
        client.InviteExpiresAt = null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (owner is null)
        {
            return new LinkResult(LinkOutcome.TakenByOtherClient, null);
        }

        return new LinkResult(owner is null ? LinkOutcome.Linked : LinkOutcome.AlreadyLinked, client.Id);
    }

    public Task<Guid?> ClientOfAsync(ExternalIdentity identity, CancellationToken ct = default) =>
        db.ClientLinks.AsNoTracking()
            .Where(link => link.Provider == identity.Provider && link.Subject == identity.Subject)
            .Select(link => (Guid?)link.ClientId)
            .SingleOrDefaultAsync(ct);

    public Task<List<ClientLink>> ListAsync(Guid clientId, CancellationToken ct = default) =>
        db.ClientLinks.AsNoTracking()
            .Where(link => link.ClientId == clientId)
            .OrderBy(link => link.LinkedAt).ThenBy(link => link.Provider)
            .ToListAsync(ct);

    public async Task UnlinkAsync(Guid clientId, string provider, CancellationToken ct = default)
    {
        var name = provider.Trim().ToLowerInvariant();
        var links = await db.ClientLinks.Where(link => link.ClientId == clientId && link.Provider == name).ToListAsync(ct);
        if (links.Count == 0)
            throw new NotFoundException(string.Format(CoreText.LinkNotFound, name));

        db.ClientLinks.RemoveRange(links);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Client?> FindInvitedAsync(string? code, CancellationToken ct)
    {
        if (!InviteCode.IsWellFormed(code))
            return null;

        var hash = InviteCode.Hash(code);
        var now = clock.GetUtcNow();
        var client = await db.Clients.SingleOrDefaultAsync(saved => saved.InviteHash == hash, ct);
        return client?.InviteExpiresAt > now ? client : null;
    }
}
