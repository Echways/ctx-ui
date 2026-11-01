using MudBlazor;

namespace CtxUi.Extensions;

public sealed record PanelActionArgs<TContext>(TContext Context, IDialogService Dialogs, IServiceProvider Services);
