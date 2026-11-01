using System.Collections.Frozen;

namespace CtxUi.Extensions;

internal sealed record SlotAction<TContext>(Func<string> Title, Func<PanelActionArgs<TContext>, Task> Run, int Order);

internal sealed class PanelSlotRegistry
{
    private readonly Dictionary<string, List<(Type Block, int Order)>> _pendingBlocks = [];
    private readonly Dictionary<string, object> _pendingActions = [];
    private FrozenDictionary<string, Type[]> _blocks = FrozenDictionary<string, Type[]>.Empty;
    private FrozenDictionary<string, object> _actions = FrozenDictionary<string, object>.Empty;

    public void AddBlock(string slot, Type block, int order)
    {
        if (!_pendingBlocks.TryGetValue(slot, out var blocks))
            _pendingBlocks[slot] = blocks = [];
        blocks.Add((block, order));
    }

    public void AddAction<TContext>(string slot, SlotAction<TContext> action)
    {
        if (!_pendingActions.TryGetValue(slot, out var found))
            _pendingActions[slot] = found = new List<SlotAction<TContext>>();
        var actions = (List<SlotAction<TContext>>)found;
        var later = actions.FindIndex(existing => existing.Order > action.Order);
        actions.Insert(later < 0 ? actions.Count : later, action);
    }

    public void Freeze()
    {
        _blocks = _pendingBlocks.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.OrderBy(entry => entry.Order).Select(entry => entry.Block).ToArray());
        _actions = _pendingActions.ToFrozenDictionary();
    }

    public IReadOnlyList<Type> Blocks(string slot) => _blocks.GetValueOrDefault(slot) ?? [];

    public IReadOnlyList<SlotAction<TContext>> Actions<TContext>(string slot) =>
        _actions.GetValueOrDefault(slot) as List<SlotAction<TContext>> ?? [];
}
