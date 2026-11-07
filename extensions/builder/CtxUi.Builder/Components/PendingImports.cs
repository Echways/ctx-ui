using CtxUi.Builder.Tools;

namespace CtxUi.Builder.Components;

public sealed class PendingImports
{
    private readonly Dictionary<Guid, ImportedApi> _waiting = [];

    public void Put(Guid serverId, ImportedApi api)
    {
        _waiting[serverId] = api;
    }

    public ImportedApi? Take(Guid serverId)
    {
        return _waiting.Remove(serverId, out var api) ? api : null;
    }
}
