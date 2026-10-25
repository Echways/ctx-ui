using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Routing;

public sealed record InboundRoute(
    Guid Id,
    string Slug,
    Uri Url,
    IReadOnlyDictionary<string, string> UpstreamHeaders,
    bool ForwardIdentity,
    TimeSpan RequestTimeout)
{
    public int MaxBodyBytes { get; init; } = Inbound.DefaultMaxBodyBytes;
}

public sealed record ClientAccess(
    Guid Id,
    string Name,
    bool Enabled,
    DateTimeOffset? ExpiresAt,
    FrozenSet<Guid> InboundIds,
    IReadOnlyDictionary<string, string> Metadata)
{
    public ClientLimits Limits { get; init; } = ClientLimits.None;

    public DateTimeOffset PeriodStartedAt { get; init; }

    public bool DirectKey { get; init; } = true;

    public int? MaxDevices { get; init; }
}

public enum AccessStatus
{
    Allowed,
    TokenMissing,
    TokenUnknown,
    ClientDisabled,
    ClientExpired,
    InboundNotFound,
    DirectKeyOff,
    TokenInvalid,
    DeviceRevoked,
}

public readonly record struct AccessDecision(
    AccessStatus Status,
    ClientAccess? Client = null,
    InboundRoute? Route = null,
    Guid? DeviceId = null);

public sealed class RoutingSnapshot
{
    private readonly FrozenDictionary<string, InboundRoute> _routes;
    private readonly FrozenDictionary<string, ClientAccess> _clients;
    private readonly FrozenDictionary<Guid, ClientAccess> _clientsById;
    private readonly FrozenDictionary<Guid, Guid> _activeDevices;

    public RoutingSnapshot(
        long revision,
        IEnumerable<InboundRoute> routes,
        IEnumerable<KeyValuePair<string, ClientAccess>> clientsByTokenHash,
        IEnumerable<KeyValuePair<Guid, Guid>>? activeDevices = null,
        SignInConfig? signIn = null)
    {
        Revision = revision;
        _routes = routes.ToFrozenDictionary(route => route.Slug, StringComparer.Ordinal);
        _clients = clientsByTokenHash.ToFrozenDictionary(StringComparer.Ordinal);
        _clientsById = _clients.Values.ToFrozenDictionary(client => client.Id);
        _activeDevices = (activeDevices ?? []).ToFrozenDictionary();
        SignIn = signIn ?? SignInConfig.Off;
    }

    public static RoutingSnapshot Empty { get; } = new(-1, [], []);

    public long Revision { get; }

    public SignInConfig SignIn { get; }

    public ClientAccess? ClientByToken(string? token) =>
        AccessToken.IsWellFormed(token) && _clients.TryGetValue(AccessToken.Hash(token), out var client) ? client : null;

    public ClientAccess? ClientById(Guid id) => _clientsById.GetValueOrDefault(id);

    public bool IsDeviceActive(Guid deviceId, Guid clientId) =>
        _activeDevices.TryGetValue(deviceId, out var owner) && owner == clientId;

    public AccessDecision Authorize(Credential credential, string slug, DateTimeOffset now)
    {
        switch (credential.Kind)
        {
            case CredentialKind.Key when string.IsNullOrEmpty(credential.Token):
            case CredentialKind.Missing:
                return new AccessDecision(AccessStatus.TokenMissing);
            case CredentialKind.Key:
                var keyed = ClientByToken(credential.Token);
                if (keyed is null)
                    return new AccessDecision(AccessStatus.TokenUnknown);
                if (!keyed.DirectKey)
                    return new AccessDecision(AccessStatus.DirectKeyOff, keyed);

                return Admit(keyed, slug, now, null);
            case CredentialKind.Grant when credential.Grant is { } grant:
                var granted = ClientById(grant.ClientId);
                if (granted is null)
                    return new AccessDecision(AccessStatus.TokenInvalid);
                if (!IsDeviceActive(grant.DeviceId, granted.Id))
                    return new AccessDecision(AccessStatus.DeviceRevoked, granted);

                return Admit(granted, slug, now, grant.DeviceId);
            default:
                return new AccessDecision(AccessStatus.TokenInvalid);
        }
    }

    private AccessDecision Admit(ClientAccess client, string slug, DateTimeOffset now, Guid? deviceId)
    {
        if (!client.Enabled)
            return new AccessDecision(AccessStatus.ClientDisabled, client);
        if (client.ExpiresAt is { } expiresAt && now >= expiresAt)
            return new AccessDecision(AccessStatus.ClientExpired, client);
        if (!_routes.TryGetValue(slug, out var route) || !client.InboundIds.Contains(route.Id))
            return new AccessDecision(AccessStatus.InboundNotFound, client);

        return new AccessDecision(AccessStatus.Allowed, client, route, deviceId);
    }
}
