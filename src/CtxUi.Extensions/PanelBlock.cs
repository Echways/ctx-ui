using Microsoft.AspNetCore.Components;

namespace CtxUi.Extensions;

public abstract class PanelBlock<TContext> : ComponentBase
{
    [Parameter, EditorRequired]
    public TContext Context { get; set; } = default!;
}
