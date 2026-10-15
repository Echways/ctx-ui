using Microsoft.Playwright;

namespace CtxUi.IntegrationTests;

public static class RowMenu
{
    public static async Task ChooseAsync(ILocator row, string action)
    {
        await row.GetByRole(AriaRole.Button, new() { Name = "Ещё действия" }).ClickAsync();
        await row.Page.GetByRole(AriaRole.Menuitem, new() { Name = action }).ClickAsync();
    }
}
