using Microsoft.Playwright;

namespace CtxUi.IntegrationTests;

public static class RowMenu
{
    public static async Task ChooseAsync(ILocator row, string action)
    {
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Ещё действия" }).ClickAsync();
        await row.Page.GetByRole(AriaRole.Menuitem, new PageGetByRoleOptions { Name = action }).ClickAsync();
    }
}
