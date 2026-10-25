using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using CtxUi.Core;
using CtxUi.Core.SignIn;

namespace CtxUi.Server.Api;

public sealed record ProviderView(string Name, string Title, bool Enabled, string ClientId, bool SecretSet, string? Issuer, string CallbackUrl);

public sealed record OAuthView(
    bool Enabled,
    bool Available,
    bool KeySignIn,
    int? DefaultMaxDevices,
    bool DefaultDirectKey,
    int IdleDays,
    bool SelfRevoke,
    IReadOnlyList<ProviderView> Providers);

public sealed record OAuthChange(
    bool? Enabled = null,
    bool? KeySignIn = null,
    string? DefaultMaxDevices = null,
    bool? DefaultDirectKey = null,
    int? IdleDays = null,
    bool? SelfRevoke = null);

public sealed record ProviderBody(string? ClientId = null, string? ClientSecret = null, string? Issuer = null, string? Title = null);

public static class OAuthEndpoints
{
    public static void MapOAuth(this RouteGroupBuilder api)
    {
        var oauth = api.MapGroup("/oauth").WithTags("oauth");
        oauth.MapGet("/", GetAsync).WithSummary("Browser sign-in settings and providers; secrets are never returned");
        oauth.MapPatch("/", ChangeAsync).WithSummary("Change the sign-in settings").WithDescription(
            "A field left out is not changed. defaultMaxDevices: a number or none. Turning OAuth on needs a public address with https://.");
        oauth.MapPut("/providers/{name}", PutProviderAsync).WithSummary("Set up a provider").WithDescription(
            "A field left out keeps its stored value; a new provider needs clientId and clientSecret. issuer and title are for oidc only.");
        oauth.MapPost("/providers/{name}/enable", (string name, SignInSettingsService service, CancellationToken ct) => SwitchAsync(service, name, true, ct))
            .WithSummary("Enable a provider");
        oauth.MapPost("/providers/{name}/disable", (string name, SignInSettingsService service, CancellationToken ct) => SwitchAsync(service, name, false, ct))
            .WithSummary("Disable a provider");
        oauth.MapDelete("/providers/{name}", RemoveProviderAsync).WithSummary("Remove a provider and the accounts linked through it");
    }

    private static async Task<Ok<OAuthView>> GetAsync(SignInSettingsService service, IOptions<CtxUiOptions> options, CancellationToken ct) =>
        TypedResults.Ok(await ViewAsync(service, options.Value.PublicUrl, ct));

    private static async Task<Ok<OAuthView>> ChangeAsync(
        OAuthChange body, SignInSettingsService service, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var devices = body.DefaultMaxDevices;
        await service.UpdateAsync(
            new SignInSettingsUpdate
            {
                Enabled = body.Enabled,
                KeySignIn = body.KeySignIn,
                ChangeDefaultMaxDevices = devices is not null,
                DefaultMaxDevices = devices is null || devices == LimitArgument.None ? null : DefaultDevices(devices),
                DefaultDirectKey = body.DefaultDirectKey,
                IdleDays = body.IdleDays,
                SelfRevoke = body.SelfRevoke,
            },
            options.Value.PublicUrl,
            ct);
        return TypedResults.Ok(await ViewAsync(service, options.Value.PublicUrl, ct));
    }

    private static async Task<Ok<ProviderView>> PutProviderAsync(
        string name, ProviderBody body, SignInSettingsService service, IOptions<CtxUiOptions> options, CancellationToken ct)
    {
        var info = ProviderCatalog.Get(name);
        var stored = (await service.ProvidersAsync(ct)).FirstOrDefault(provider => provider.Name == info.Name);
        await service.SetProviderAsync(
            new ProviderSetup(info.Name, body.ClientId ?? stored?.ClientId ?? "", body.ClientSecret, body.Issuer ?? stored?.Issuer, body.Title ?? stored?.Title),
            ct);
        var saved = (await service.ProvidersAsync(ct)).Single(provider => provider.Name == info.Name);
        return TypedResults.Ok(Provider(saved, options.Value.PublicUrl));
    }

    private static async Task<NoContent> SwitchAsync(SignInSettingsService service, string name, bool enabled, CancellationToken ct)
    {
        await service.SetProviderEnabledAsync(name, enabled, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RemoveProviderAsync(string name, SignInSettingsService service, CancellationToken ct)
    {
        await service.RemoveProviderAsync(name, ct);
        return TypedResults.NoContent();
    }

    private static int DefaultDevices(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DomainException(string.Format(ServerText.DefaultDevicesInvalid, text));

    private static async Task<OAuthView> ViewAsync(SignInSettingsService service, string publicUrl, CancellationToken ct)
    {
        var settings = await service.GetAsync(ct);
        var providers = await service.ProvidersAsync(ct);
        return new OAuthView(
            settings.Enabled,
            OAuthAvailability.IsAllowed(publicUrl),
            settings.KeySignIn,
            settings.DefaultMaxDevices,
            settings.DefaultDirectKey,
            settings.IdleDays,
            settings.SelfRevoke,
            providers.Select(provider => Provider(provider, publicUrl)).ToList());
    }

    private static ProviderView Provider(SignInProvider provider, string publicUrl) => new(
        provider.Name,
        provider.Title ?? ProviderCatalog.Get(provider.Name).Title,
        provider.Enabled,
        provider.ClientId,
        provider.SecretSealed.Length > 0,
        provider.Issuer,
        $"{OAuthResource.Issuer(publicUrl)}/connect/callback/{provider.Name}");
}
