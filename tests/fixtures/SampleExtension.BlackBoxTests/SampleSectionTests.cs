using Microsoft.Playwright;
using CtxUi.Extensions.Testing;
using static Microsoft.Playwright.Assertions;

namespace SampleExtension.BlackBoxTests;

public class SampleSectionTests
{
    private static TestPanelOptions Options(IReadOnlyDictionary<string, string>? settings = null) =>
        new(Path.Combine(AppContext.BaseDirectory, "extensions"), settings);

    [Fact]
    public async Task A_section_opens_in_a_real_server_and_works()
    {
        await using var panel = await TestPanel.StartAsync(Options());
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        await panel.SignInAsync(page);
        await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Образец" }).ClickAsync();

        await Expect(page.GetByTestId("sample-note")).ToHaveTextAsync("Раздел-образец");
        await Expect(page.GetByTestId("sample-context")).ToHaveTextAsync("sample");
        await page.GetByRole(AriaRole.Button, new() { Name = "Нажать" }).ClickAsync();
        await Expect(page.GetByTestId("sample-clicks")).ToHaveTextAsync("1");
        Assert.DoesNotContain("fail:", panel.Log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_command_line_works_against_the_same_data()
    {
        await using var panel = await TestPanel.StartAsync(Options());

        Assert.Contains("sample  1.2.3  ok", await panel.CliAsync("extension", "list"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_reach_the_extension()
    {
        await using var panel = await TestPanel.StartAsync(Options(new Dictionary<string, string> { ["CtxUi:Extensions:sample:Reshape"] = "true" }));
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();

        await panel.SignInAsync(page);

        await Expect(page.GetByRole(AriaRole.Banner).GetByText("Sample brand")).ToBeVisibleAsync();
    }
}
