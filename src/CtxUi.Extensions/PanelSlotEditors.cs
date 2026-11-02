using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Extensions;

public sealed class PanelBlockSlotEditor<TContext>
{
    private readonly PanelEditor _editor;
    private readonly PanelSlotRegistry _registry;
    private readonly string _slot;

    internal PanelBlockSlotEditor(PanelEditor editor, PanelSlotRegistry registry, string slot)
    {
        _editor = editor;
        _registry = registry;
        _slot = slot;
    }

    public PanelEditor AddBlock<TBlock>(int order = 0) where TBlock : PanelBlock<TContext>
    {
        _registry.AddBlock(_slot, typeof(TBlock), order);
        return _editor;
    }
}

public sealed class PanelActionSlotEditor<TContext>
{
    private readonly PanelEditor _editor;
    private readonly PanelSlotRegistry _registry;
    private readonly string _slot;

    internal PanelActionSlotEditor(PanelEditor editor, PanelSlotRegistry registry, string slot)
    {
        _editor = editor;
        _registry = registry;
        _slot = slot;
    }

    public PanelEditor AddAction(Func<string> title, Func<PanelActionArgs<TContext>, Task> run, int order = 0, Func<TContext, bool>? visible = null)
    {
        _registry.AddAction(_slot, new SlotAction<TContext>(title, run, order, visible));
        return _editor;
    }

    public PanelEditor AddSheet<TSheet>(Func<string> title, Func<TContext, string> sheetTitle, int order = 0, Func<TContext, bool>? visible = null)
        where TSheet : PanelBlock<TContext> =>
        AddAction(
            title,
            args => args.Services.GetRequiredService<IBlockSheets>().ShowAsync(sheetTitle(args.Context), typeof(TSheet), args.Context),
            order,
            visible);
}
