using System.Collections.Concurrent;

namespace CtxUi.Builder.Servers;

public sealed class ConnectedInbounds
{
    private readonly ConcurrentDictionary<Guid, byte> _ids = new();

    public bool Contains(Guid inboundId)
    {
        return _ids.ContainsKey(inboundId);
    }

    public void Add(Guid inboundId)
    {
        _ids[inboundId] = 0;
    }

    public void Remove(Guid inboundId)
    {
        _ids.TryRemove(inboundId, out _);
    }

    public void Replace(IEnumerable<Guid> inboundIds)
    {
        var wanted = inboundIds.ToHashSet();
        foreach (var known in _ids.Keys.Where(id => !wanted.Contains(id)))
            _ids.TryRemove(known, out _);
        foreach (var id in wanted)
            _ids[id] = 0;
    }
}
