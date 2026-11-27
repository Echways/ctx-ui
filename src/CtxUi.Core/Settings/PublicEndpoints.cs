using System.Collections.Frozen;
using System.Collections.Immutable;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Settings;

public sealed record PublicResource(string Url, InboundRoute Inbound);

public sealed class PublicEndpoints
{
    public const string DefaultId = "default";

    private readonly FrozenDictionary<string, HarnessProfile> _profiles;
    private readonly FrozenDictionary<string, string> _origins;
    private readonly string? _normalizedGlobalUrl;

    public PublicEndpoints(string globalUrl, IEnumerable<HarnessProfile> profiles, bool legacy)
    {
        GlobalUrl = OAuthResource.Issuer(globalUrl);
        _normalizedGlobalUrl = PublicAddress.Normalize(GlobalUrl);
        Legacy = legacy;
        Profiles = new[] { new HarnessProfile(DefaultId, "Default", true, null) }
            .Concat(profiles.OrderBy(profile => profile.Id, StringComparer.Ordinal)).ToImmutableArray();
        _profiles = Profiles.ToFrozenDictionary(profile => profile.Id, StringComparer.Ordinal);
        _origins = Profiles.Where(profile => profile.Enabled)
            .Select(EffectiveUrl)
            .Distinct(StringComparer.Ordinal)
            .Select(url => (Url: url, Uri: PublicAddress.Parse(url)))
            .Where(entry => entry.Uri is not null)
            .GroupBy(entry => PublicAddress.Authority(entry.Uri!), StringComparer.Ordinal)
            .ToFrozenDictionary(group => group.Key, group => group.First().Url, StringComparer.Ordinal);
    }

    public static PublicEndpoints Default { get; } = new(PublicEndpointOptions.DefaultUrl, [], true);

    public string GlobalUrl { get; }
    public bool Legacy { get; }
    public ImmutableArray<HarnessProfile> Profiles { get; }

    public string EffectiveUrl(HarnessProfile profile)
    {
        return profile.PublicUrl is { } url && url != _normalizedGlobalUrl ? url : GlobalUrl;
    }

    public string Select(string? id)
    {
        if (!_profiles.TryGetValue(id ?? DefaultId, out var profile) || !profile.Enabled)
            throw new DomainException(CoreText.HarnessUnavailable);

        return EffectiveUrl(profile);
    }

    public string? ForRequest(string scheme, string? host)
    {
        if (PublicAddress.Parse($"{scheme}://{host}") is { AbsolutePath: "/" } uri &&
            _origins.TryGetValue(PublicAddress.Authority(uri), out var url))
            return url;

        return Legacy ? GlobalUrl : null;
    }

    public (string Url, string Slug)? Match(string? resource)
    {
        if (PublicAddress.Parse(resource) is not { } uri ||
            !_origins.TryGetValue(PublicAddress.Authority(uri), out var url) ||
            !OAuthAvailability.IsAllowed(url) ||
            !OAuthResource.TryPath(url, resource!, out var slug))
            return null;

        return (OAuthResource.For(url, slug), slug);
    }

    public PublicResource? Resolve(RoutingSnapshot snapshot, string? resource)
    {
        return Match(resource) is { } match && snapshot.Route(match.Slug) is { } inbound
            ? new PublicResource(match.Url, inbound)
            : null;
    }
}
