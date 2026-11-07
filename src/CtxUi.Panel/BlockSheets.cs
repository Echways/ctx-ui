using Enfilade;
using MudBlazor;
using CtxUi.Extensions;
using CtxUi.Panel.Components;

namespace CtxUi.Panel;

internal sealed class BlockSheets(IDialogService dialogs) : IBlockSheets
{
    public Task ShowAsync(string title, Type block, object? context)
    {
        return dialogs.ShowSheetAsync(
            title,
            new DialogParameters<BlockSheet>
            {
                { sheet => sheet.Block, block },
                { sheet => sheet.Context, context }
            },
            false);
    }
}
