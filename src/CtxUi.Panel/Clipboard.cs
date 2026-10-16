using Microsoft.JSInterop;
using MudBlazor;

namespace CtxUi.Panel;

public sealed class Clipboard(IJSRuntime js, ISnackbar snackbar)
{
    public static readonly TimeSpan Acknowledged = TimeSpan.FromSeconds(1.5);

    public async Task<bool> CopyAsync(string text)
    {
        try
        {
            await js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return true;
        }
        catch (JSException)
        {
            snackbar.Add(PanelText.CopyNeedsHttps, Severity.Warning);
            return false;
        }
    }
}
