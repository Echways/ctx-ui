using Microsoft.AspNetCore.Components;
using Enfilade;

namespace CtxUi.Extensions;

public sealed class PanelEditor
{
    private readonly PanelLayout _layout;
    private readonly string _assemblyName;
    private string? _lastAdded;
    private int _captions;

    internal PanelEditor(PanelLayout layout, string assemblyName)
    {
        _layout = layout;
        _assemblyName = assemblyName;
    }

    public PanelEditor AddSection<TPage>(Func<string> title, PanelPosition? at = null,
        Action<SectionSetup>? setup = null)
        where TPage : IComponent
    {
        _lastAdded = _layout.InsertSection<TPage>(PanelSections.Of<TPage>(), title, setup, at, _lastAdded);
        return this;
    }

    public PanelEditor AddCaption(Func<string> title, PanelPosition? at = null)
    {
        _lastAdded = _layout.InsertCaption($"{_assemblyName}#{++_captions}", title, at, _lastAdded);
        return this;
    }

    public PanelEditor Hide(string entry)
    {
        _layout.Hide(entry);
        return this;
    }

    public PanelEditor Move(string entry, PanelPosition at)
    {
        _layout.Move(entry, at);
        return this;
    }

    public PanelEditor Rename(string entry, Func<string> title)
    {
        _layout.Rename(entry, title);
        return this;
    }

    public PanelEditor AddAccountLink(Func<string> title, string href)
    {
        _layout.AddAccountLink(title, href);
        return this;
    }

    public PanelEditor AddStylesheet(string path)
    {
        _layout.AddStylesheet($"_content/{_assemblyName}/{path.TrimStart('/')}");
        return this;
    }

    public PanelBlockSlotEditor<TContext> In<TContext>(PanelSlot<TContext> slot)
    {
        return new PanelBlockSlotEditor<TContext>(this, _layout.Slots, slot.Name);
    }

    public PanelActionSlotEditor<TContext> In<TContext>(PanelActionSlot<TContext> slot)
    {
        return new PanelActionSlotEditor<TContext>(this, _layout.Slots, slot.Name);
    }

    public PanelEditor Customize(Action<PanelLook> look)
    {
        _layout.Customize(look);
        return this;
    }
}
