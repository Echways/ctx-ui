using System.Collections.Frozen;
using CtxUi.Core.Clients;

namespace CtxUi.Core.Routing;

public sealed record InboundRoute(
    Guid Id,
    string Slug,
    Uri Url,
    IReadOnlyDictionary<string, string> UpstreamHeaders,
    bool ForwardIdentity,
    TimeSpan RequestTimeout);

public sealed record ClientAccess(
    Guid Id,
    string Name,
    bool Enabled,
    DateTimeOffset? ExpiresAt,
    FrozenSet<Guid> InboundIds,
    IReadOnlyDictionary<string, string> Metadata);

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

/// <summary>
/// Неизменяемая таблица маршрутов и клиентов. Шлюз читает её на каждом запросе без обращения к базе;
/// после изменения в базе строится новый снимок и подменяет старый.
/// </summary>
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

    /// <summary>Снимок до первой загрузки. Ревизия -1 не совпадает ни с одной ревизией в базе.</summary>
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
