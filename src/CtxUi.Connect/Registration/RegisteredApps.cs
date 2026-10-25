using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using CtxUi.Core.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect.Registration;

public sealed record RegistrationResult(
    string? ClientId,
    string ClientName,
    IReadOnlyList<string> RedirectUris,
    string? Error = null,
    string? ErrorDescription = null);

public sealed class RegisteredApps(
    IOpenIddictApplicationManager apps,
    ClientMetadataFetcher fetcher,
    IMetadataAddressPolicy policy,
    CtxUiDbContext db,
    TimeProvider clock)
{
    public const string DynamicClientPrefix = "dcr_";

    private const string CachedUntilProperty = "ctx_cached_until";
    private const string CreatedProperty = "ctx_created";
    private const int DynamicIdLength = 32;

    private static readonly string[] GrantTypes = [OpenIddictConstants.GrantTypes.AuthorizationCode, OpenIddictConstants.GrantTypes.RefreshToken];
    private static readonly string[] ResponseTypes = [OpenIddictConstants.ResponseTypes.Code];

    public bool IsDocumentClientId(string? clientId, out Uri url) =>
        Uri.TryCreate(clientId, UriKind.Absolute, out url!) && url.OriginalString == clientId && policy.AllowsUri(url);

    public async Task<bool> EnsureFromDocumentAsync(string clientId, CancellationToken ct)
    {
        if (!IsDocumentClientId(clientId, out var url))
            return false;

        var now = clock.GetUtcNow();
        var app = await apps.FindByClientIdAsync(clientId, ct);
        if (app is not null && Moment(await apps.GetPropertiesAsync(app, ct), CachedUntilProperty) > now)
            return true;

        if (await fetcher.FetchAsync(url, ct) is not { } document)
            return false;

        var descriptor = Describe(document.ClientId, document.ClientName, document.RedirectUris, now);
        descriptor.Properties[CachedUntilProperty] = JsonSerializer.SerializeToElement(now + document.CacheFor);
        if (app is null)
            await apps.CreateAsync(descriptor, ct);
        else
            await apps.UpdateAsync(app, descriptor, ct);

        return true;
    }

    public async Task<RegistrationResult> RegisterAsync(JsonElement request, CancellationToken ct)
    {
        if (request.ValueKind != JsonValueKind.Object)
            return Refused("", [], "invalid_client_metadata", "the body must be a JSON object");

        var uris = new List<string>();
        if (request.TryGetProperty("redirect_uris", out var redirects) && redirects.ValueKind == JsonValueKind.Array)
        {
            foreach (var redirect in redirects.EnumerateArray())
            {
                if (redirect.ValueKind != JsonValueKind.String || !RedirectUris.IsAllowedForRegistration(redirect.GetString()!))
                    return Refused("", [], "invalid_redirect_uri", "redirect URIs must be https, loopback http or an app scheme");

                uris.Add(redirect.GetString()!);
            }
        }

        if (uris.Count == 0)
            return Refused("", [], "invalid_redirect_uri", "at least one redirect URI is required");
        if (Text(request, "token_endpoint_auth_method") is not (null or ClientAuthenticationMethods.None or ClientAuthenticationMethods.ClientSecretPost or ClientAuthenticationMethods.ClientSecretBasic))
            return Refused("", uris, "invalid_client_metadata", "only public clients are registered, the token endpoint authentication method is always none");
        if (!Subset(request, "grant_types", GrantTypes) || !Subset(request, "response_types", ResponseTypes))
            return Refused("", uris, "invalid_client_metadata", "only the authorization code flow with refresh tokens is supported");

        uris = uris.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var name = Text(request, "client_name")?.Trim() is { Length: > 0 } given ? given : AppIdentity.Of(null, "", uris).RedirectHosts[0];
        var clientId = DynamicClientPrefix + AppIdentity.Fingerprint(name, uris)[..DynamicIdLength];
        if (await apps.FindByClientIdAsync(clientId, ct) is null)
            await apps.CreateAsync(Describe(clientId, name, uris, clock.GetUtcNow()), ct);

        return new RegistrationResult(clientId, name, uris);
    }

    public async Task<AppIdentity?> IdentityAsync(string clientId, CancellationToken ct)
    {
        if (await apps.FindByClientIdAsync(clientId, ct) is not { } app)
            return null;

        var name = await apps.GetDisplayNameAsync(app, ct) ?? clientId;
        var uris = await apps.GetRedirectUrisAsync(app, ct);
        return AppIdentity.Of(IsDocumentClientId(clientId, out _) ? clientId : null, name, uris);
    }

    public async Task<string?> ApplicationIdAsync(string clientId, CancellationToken ct) =>
        await apps.FindByClientIdAsync(clientId, ct) is { } app ? await apps.GetIdAsync(app, ct) : null;

    public async Task<int> PruneAsync(DateTimeOffset before, CancellationToken ct)
    {
        var unused = await db.Set<OpenIddictEntityFrameworkCoreApplication>()
            .Where(app => app.ClientId!.StartsWith(DynamicClientPrefix) && !app.Authorizations.Any())
            .ToListAsync(ct);
        var removed = 0;
        foreach (var app in unused)
        {
            if (Moment(await apps.GetPropertiesAsync(app, ct), CreatedProperty) is not { } created || created >= before)
                continue;

            await apps.DeleteAsync(app, ct);
            removed++;
        }

        return removed;
    }

    private static OpenIddictApplicationDescriptor Describe(string clientId, string name, IEnumerable<string> redirectUris, DateTimeOffset now)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            DisplayName = name,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + ConnectServices.McpScope,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var uri in redirectUris)
            descriptor.RedirectUris.Add(new Uri(uri));

        descriptor.Properties[CreatedProperty] = JsonSerializer.SerializeToElement(now);
        return descriptor;
    }

    private static DateTimeOffset? Moment(ImmutableDictionary<string, JsonElement> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.TryGetDateTimeOffset(out var moment) ? moment : null;

    private static string? Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Subset(JsonElement request, string name, string[] allowed) =>
        !request.TryGetProperty(name, out var values)
        || values.ValueKind == JsonValueKind.Array
        && values.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()));

    private static RegistrationResult Refused(string name, IReadOnlyList<string> uris, string error, string description) =>
        new(null, name, uris, error, description);
}
