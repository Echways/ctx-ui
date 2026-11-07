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

    public PanelEditor AddAction(Func<string> title, Func<PanelActionArgs<TContext>, Task> run, int order = 0)
    {
        _registry.AddAction(_slot, new SlotAction<TContext>(title, run, order));
        return _editor;
    }

    public PanelEditor AddSheet<TSheet>(Func<string> title, Func<TContext, string> sheetTitle, int order = 0)
        where TSheet : PanelBlock<TContext>
    {
        return AddAction(title, OpenSheet<TSheet>(sheetTitle), order);
    }

    public PanelConditionalActionEditor<TContext> When(Func<TContext, bool> visible)
    {
        return new PanelConditionalActionEditor<TContext>(_editor, _registry, _slot, visible);
    }

    internal static Func<PanelActionArgs<TContext>, Task> OpenSheet<TSheet>(Func<TContext, string> sheetTitle)
        where TSheet : PanelBlock<TContext>
    {
        return args => args.Services.GetRequiredService<IBlockSheets>()
            .ShowAsync(sheetTitle(args.Context), typeof(TSheet), args.Context);
    }
}

public sealed class PanelConditionalActionEditor<TContext>
{
    private readonly PanelEditor _editor;
    private readonly PanelSlotRegistry _registry;
    private readonly string _slot;
    private readonly Func<TContext, bool> _visible;

    internal PanelConditionalActionEditor(PanelEditor editor, PanelSlotRegistry registry, string slot,
        Func<TContext, bool> visible)
    {
        _editor = editor;
        _registry = registry;
        _slot = slot;
        _visible = visible;
    }

    public PanelEditor AddAction(Func<string> title, Func<PanelActionArgs<TContext>, Task> run, int order = 0)
    {
        _registry.AddAction(_slot, new SlotAction<TContext>(title, run, order, _visible));
        return _editor;
    }

    public PanelEditor AddSheet<TSheet>(Func<string> title, Func<TContext, string> sheetTitle, int order = 0)
        where TSheet : PanelBlock<TContext>
    {
        return AddAction(title, PanelActionSlotEditor<TContext>.OpenSheet<TSheet>(sheetTitle), order);
    }
}
