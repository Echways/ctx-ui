using MudBlazor;
using CtxUi.Core;

namespace CtxUi.Panel;

public sealed class PanelActions(PanelBackend backend, ISnackbar snackbar)
{
    public async Task<bool> ApplyAsync(Func<IServiceProvider, Task> change, string done)
    {
        try
        {
            await backend.ChangeAsync(change);
            snackbar.Add(done, Severity.Success);
            return true;
        }
        catch (DomainException exception)
        {
            snackbar.Add(Words.Sentence(exception.Message), Severity.Error);
            return false;
        }
    }
}
