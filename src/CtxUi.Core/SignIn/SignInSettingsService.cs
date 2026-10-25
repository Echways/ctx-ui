using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Core.Secrets;

namespace CtxUi.Core.SignIn;

public sealed record SignInSettingsUpdate
{
    public bool? Enabled { get; init; }

    public bool? KeySignIn { get; init; }

    public bool ChangeDefaultMaxDevices { get; init; }

    public int? DefaultMaxDevices { get; init; }

    public bool? DefaultDirectKey { get; init; }

    public int? IdleDays { get; init; }

    public bool? SelfRevoke { get; init; }
}

public sealed record ProviderSetup(string Name, string ClientId, string? Secret, string? Issuer = null, string? Title = null);

public sealed class SignInSettingsService(CtxUiDbContext db, ISecretVault vault)
{
    public Task<SignInSettings> GetAsync(CancellationToken ct = default) =>
        db.SignInSettings.AsNoTracking().SingleAsync(ct);

    public async Task UpdateAsync(SignInSettingsUpdate update, string publicUrl, CancellationToken ct = default)
    {
        var settings = await db.SignInSettings.SingleAsync(ct);
        if (update.Enabled == true && !OAuthAvailability.IsAllowed(publicUrl))
            throw new DomainException(CoreText.OAuthNeedsHttps);
        if (update.IdleDays is < 1 or > SignInSettings.MaxIdleDays)
            throw new DomainException(string.Format(CoreText.IdleDaysOutOfRange, SignInSettings.MaxIdleDays));

        settings.Enabled = update.Enabled ?? settings.Enabled;
        settings.KeySignIn = update.KeySignIn ?? settings.KeySignIn;
        if (update.ChangeDefaultMaxDevices)
            settings.DefaultMaxDevices = DeviceLimit.Validated(update.DefaultMaxDevices);
        settings.DefaultDirectKey = update.DefaultDirectKey ?? settings.DefaultDirectKey;
        settings.IdleDays = update.IdleDays ?? settings.IdleDays;
        settings.SelfRevoke = update.SelfRevoke ?? settings.SelfRevoke;

        await EnsureSignInMethodAsync(settings, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<SignInProvider>> ProvidersAsync(CancellationToken ct = default) =>
        (await db.SignInProviders.AsNoTracking().ToListAsync(ct))
        .OrderBy(provider => ProviderCatalog.Order(provider.Name))
        .ToList();

    public async Task SetProviderAsync(ProviderSetup setup, CancellationToken ct = default)
    {
        var info = ProviderCatalog.Get(setup.Name);
        var clientId = setup.ClientId.Trim();
        if (clientId.Length == 0)
            throw new DomainException(CoreText.ProviderClientIdRequired);

        var issuer = setup.Issuer?.Trim();
        if (info.NeedsIssuer && !(Uri.TryCreate(issuer, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps))
            throw new DomainException(CoreText.OidcIssuerRequired);

        var secret = string.IsNullOrWhiteSpace(setup.Secret) ? null : setup.Secret.Trim();
        var provider = await db.SignInProviders.SingleOrDefaultAsync(saved => saved.Name == info.Name, ct);
        if (provider is null)
        {
            provider = new SignInProvider
            {
                Name = info.Name,
                ClientId = clientId,
                SecretSealed = vault.Seal(secret ?? throw new DomainException(CoreText.ProviderSecretRequired)),
            };
            db.SignInProviders.Add(provider);
        }
        else
        {
            provider.ClientId = clientId;
            if (secret is not null)
                provider.SecretSealed = vault.Seal(secret);
        }

        provider.Issuer = info.NeedsIssuer ? issuer : null;
        provider.Title = info.NeedsIssuer && !string.IsNullOrWhiteSpace(setup.Title) ? setup.Title.Trim() : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetProviderEnabledAsync(string name, bool enabled, CancellationToken ct = default)
    {
        var provider = await TrackProviderAsync(name, ct);
        provider.Enabled = enabled;
        await EnsureSignInMethodAsync(await db.SignInSettings.SingleAsync(ct), ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveProviderAsync(string name, CancellationToken ct = default)
    {
        var provider = await TrackProviderAsync(name, ct);
        db.SignInProviders.Remove(provider);
        await EnsureSignInMethodAsync(await db.SignInSettings.SingleAsync(ct), ct);
        db.ClientLinks.RemoveRange(await db.ClientLinks.Where(link => link.Provider == provider.Name).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }

    public Task<int> ClientsWithoutDirectKeyAsync(CancellationToken ct = default) =>
        db.Clients.CountAsync(client => !client.DirectKey, ct);

    private async Task<SignInProvider> TrackProviderAsync(string name, CancellationToken ct)
    {
        var info = ProviderCatalog.Get(name);
        return await db.SignInProviders.SingleOrDefaultAsync(provider => provider.Name == info.Name, ct)
            ?? throw new NotFoundException(string.Format(CoreText.ProviderNotConfigured, info.Name));
    }

    private async Task EnsureSignInMethodAsync(SignInSettings settings, CancellationToken ct)
    {
        if (!settings.Enabled || settings.KeySignIn)
            return;

        var providers = await db.SignInProviders.ToListAsync(ct);
        var anyEnabled = providers.Any(provider => provider.Enabled && db.Entry(provider).State != EntityState.Deleted);
        if (!anyEnabled)
            throw new DomainException(CoreText.LastSignInMethod);
    }
}
