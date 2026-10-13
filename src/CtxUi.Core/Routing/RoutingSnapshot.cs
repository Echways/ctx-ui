using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;

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
}

public enum AccessStatus
{
    Allowed,
    TokenMissing,
    TokenUnknown,
    ClientDisabled,
    ClientExpired,
    InboundNotFound,
}

public readonly record struct AccessDecision(AccessStatus Status, ClientAccess? Client = null, InboundRoute? Route = null);

public sealed class RoutingSnapshot
{
    private readonly FrozenDictionary<string, InboundRoute> _routes;
    private readonly FrozenDictionary<string, ClientAccess> _clients;

    public RoutingSnapshot(
        long revision,
        IEnumerable<InboundRoute> routes,
        IEnumerable<KeyValuePair<string, ClientAccess>> clientsByTokenHash)
    {
        Revision = revision;
        _routes = routes.ToFrozenDictionary(route => route.Slug, StringComparer.Ordinal);
        _clients = clientsByTokenHash.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public static RoutingSnapshot Empty { get; } = new(-1, [], []);

    public long Revision { get; }

    public AccessDecision Authorize(string? token, string slug, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token))
            return new AccessDecision(AccessStatus.TokenMissing);
        if (!AccessToken.IsWellFormed(token) || !_clients.TryGetValue(AccessToken.Hash(token), out var client))
            return new AccessDecision(AccessStatus.TokenUnknown);
        if (!client.Enabled)
            return new AccessDecision(AccessStatus.ClientDisabled, client);
        if (client.ExpiresAt is { } expiresAt && now >= expiresAt)
            return new AccessDecision(AccessStatus.ClientExpired, client);
        if (!_routes.TryGetValue(slug, out var route) || !client.InboundIds.Contains(route.Id))
            return new AccessDecision(AccessStatus.InboundNotFound, client);

        return new AccessDecision(AccessStatus.Allowed, client, route);
    }
}
