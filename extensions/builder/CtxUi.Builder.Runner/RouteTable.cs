using System.Collections.Immutable;
using CtxUi.Builder.Contracts;
using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace CtxUi.Builder.Runner;

public sealed class RouteTable(IServerProbe probe) : IProxyConfigProvider
{
    public const string SecretHashKey = "route-secret-sha256";

    private const string DestinationName = "server";

    private readonly object _sync = new();
    private ImmutableDictionary<RouteKey, RouteTarget> _targets = ImmutableDictionary<RouteKey, RouteTarget>.Empty;
    private Snapshot _snapshot = new([], []);

    public IProxyConfig GetConfig()
    {
        return Volatile.Read(ref _snapshot);
    }

    public void SetActive(Guid id, string container, string secretHash)
    {
        Update(targets => targets.SetItem(new RouteKey(id, null), new RouteTarget(container, secretHash)));
    }

    public void SetCandidate(Guid id, int release, string container, string secretHash)
    {
        Update(targets => targets.SetItem(new RouteKey(id, release), new RouteTarget(container, secretHash)));
    }

    public void ActivateCandidate(Guid id, int release, string container, string secretHash)
    {
        Update(targets => targets
            .Remove(new RouteKey(id, release))
            .SetItem(new RouteKey(id, null), new RouteTarget(container, secretHash)));
    }

    public void RemoveCandidate(Guid id, int release)
    {
        Update(targets => targets.Remove(new RouteKey(id, release)));
    }

    public void RemoveServer(Guid id)
    {
        Update(targets => targets.RemoveRange(targets.Keys.Where(key => key.Server == id)));
    }

    private void Update(
        Func<ImmutableDictionary<RouteKey, RouteTarget>, ImmutableDictionary<RouteKey, RouteTarget>> change)
    {
        Snapshot previous;
        lock (_sync)
        {
            _targets = change(_targets);
            previous = _snapshot;
            Volatile.Write(ref _snapshot, Build(_targets));
        }

        previous.SignalChange();
    }

    private Snapshot Build(ImmutableDictionary<RouteKey, RouteTarget> targets)
    {
        var routes = new List<RouteConfig>();
        var clusters = new List<ClusterConfig>();
        foreach (var (key, target) in targets)
        {
            var name = key.Release is { } release ? $"{key.Server:N}-r{release}" : $"{key.Server:N}-active";
            routes.Add(new RouteConfig
            {
                RouteId = name,
                ClusterId = name,
                Match = new RouteMatch
                { Path = key.Release is { } number ? $"/s/{key.Server}/r/{number}/mcp" : $"/s/{key.Server}/mcp" },
                Metadata = new Dictionary<string, string> { [SecretHashKey] = target.SecretHash },
                Transforms =
                [
                    new Dictionary<string, string> { ["PathSet"] = "/mcp" },
                    new Dictionary<string, string> { ["RequestHeaderRemove"] = RouteSecret.Header }
                ]
            });
            clusters.Add(new ClusterConfig
            {
                ClusterId = name,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    [DestinationName] = new() { Address = probe.Address(target.Container) }
                },
                HttpRequest = new ForwarderRequestConfig
                {
                    ActivityTimeout = Timeout.InfiniteTimeSpan,
                    AllowResponseBuffering = false
                }
            });
        }

        return new Snapshot(routes, clusters);
    }

    private readonly record struct RouteKey(Guid Server, int? Release);

    private readonly record struct RouteTarget(string Container, string SecretHash);

    private sealed class Snapshot(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        : IProxyConfig
    {
        private readonly CancellationTokenSource _changed = new();

        public IReadOnlyList<RouteConfig> Routes { get; } = routes;
        public IReadOnlyList<ClusterConfig> Clusters { get; } = clusters;
        public IChangeToken ChangeToken => new CancellationChangeToken(_changed.Token);

        public void SignalChange()
        {
            _changed.Cancel();
        }
    }
}
