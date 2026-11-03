using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using CtxUi.Server.Extensions;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.IntegrationTests;

public class ExtensionLoadingTests
{
    private const string Section = "Образец";

    private static readonly string[] BuiltIn = ["Обзор", "Метрики", "Inbound'ы", "Клиенты", "Журнал", "OAuth", "Ключи API"];

    private static async Task<string[]> LinksAsync(IPage page, string brand = "ctx-ui")
    {
        var links = await page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link).AllInnerTextsAsync();
        return [.. links.Select(link => link.Trim()).Where(link => link.Length > 0 && link != brand)];
    }

    private static async Task<IPage> OpenSectionAsync(GatewayHost gateway, PanelSession session)
    {
        await session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = Section }).ClickAsync();
        await Expect(session.Page.GetByTestId("sample-note")).ToBeVisibleAsync();
        return session.Page;
    }

    private static async Task AssertPanelWorksWithoutSampleAsync(GatewayHost gateway)
    {
        await using var session = await PanelSession.SignInAsync(gateway);
        Assert.Equal(BuiltIn, await LinksAsync(session.Page));
    }

    [Fact]
    public async Task An_extension_changes_the_panel_layout_and_look()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(
            new Dictionary<string, string> { ["CtxUi:Extensions:sample:Reshape"] = "true" });
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.BaseUrl) };
        using var closed = await anonymous.GetAsync($"/{gateway.PanelPath}/");
        Assert.Equal(HttpStatusCode.Redirect, closed.StatusCode);
        Assert.Contains("login", closed.Headers.Location!.ToString(), StringComparison.Ordinal);

        await using var session = await PanelSession.SignInAsync(gateway);

        Assert.Equal(
            ["Обзор", Section, "Метрики", "Inbound'ы", "Заказчики", "OAuth", "Ключи API"],
            await LinksAsync(session.Page, "Sample brand"));
        await Expect(session.Page.GetByRole(AriaRole.Banner).GetByText("Sample brand")).ToBeVisibleAsync();
        Assert.Contains("#123456", await session.Page.ContentAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hidden_section_still_opens_by_its_direct_link()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(
            new Dictionary<string, string> { ["CtxUi:Extensions:sample:Reshape"] = "true" });
        await using var session = await PanelSession.SignInAsync(gateway);

        await session.Page.GotoAsync("journal");

        await Expect(session.Page.GetByRole(AriaRole.Heading, new() { Name = "Журнал" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task An_extension_stylesheet_overrides_the_panel_one()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var links = await session.Page.Locator("link[rel=stylesheet]").EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))");

        var panel = Array.FindIndex(links, link => link.Contains("CtxUi.Panel/panel", StringComparison.Ordinal));
        var sample = Array.FindIndex(links, link => link.EndsWith("_content/SampleExtension/sample.css", StringComparison.Ordinal));
        var scoped = Array.FindIndex(links, link => link.EndsWith("_content/SampleExtension/SampleExtension.styles.css", StringComparison.Ordinal));
        Assert.True(panel >= 0, string.Join(", ", links));
        Assert.True(sample > panel, string.Join(", ", links));
        Assert.True(scoped > panel, string.Join(", ", links));
    }

    [Fact]
    public async Task An_installed_extension_adds_its_section_and_works()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        Assert.Equal(["Обзор", "Метрики", "Inbound'ы", "Клиенты", "Журнал", Section, "OAuth", "Ключи API"], await LinksAsync(session.Page));

        var page = await OpenSectionAsync(gateway, session);
        await Expect(page.GetByTestId("sample-note")).ToHaveTextAsync("Раздел-образец");
        await page.GetByRole(AriaRole.Button, new() { Name = "Нажать" }).ClickAsync();
        await Expect(page.GetByTestId("sample-clicks")).ToHaveTextAsync("1");
    }

    [Fact]
    public async Task Scoped_and_global_styles_of_an_extension_apply()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenSectionAsync(gateway, session);

        await Expect(page.GetByTestId("sample-note")).ToHaveCSSAsync("color", "rgb(1, 2, 3)");
        using var global = await gateway.Http.GetAsync($"/{gateway.PanelPath}/_content/SampleExtension/sample.css");
        Assert.Equal(HttpStatusCode.OK, global.StatusCode);
    }

    [Fact]
    public async Task An_extension_page_is_closed_to_anonymous_visitors()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        using var anonymous = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(gateway.BaseUrl) };

        using var response = await anonymous.GetAsync($"/{gateway.PanelPath}/sample");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login", response.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Its_private_dependency_loads_next_to_the_host_version()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenSectionAsync(gateway, session);

        await Expect(page.GetByTestId("sample-parser")).ToHaveTextAsync("UAParser 3.1.46");
        await Expect(page.GetByTestId("sample-context")).ToHaveTextAsync("sample");
    }

    [Fact]
    public async Task A_private_library_under_a_shared_prefix_loads_from_the_extension()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await using var session = await PanelSession.SignInAsync(gateway);

        var page = await OpenSectionAsync(gateway, session);

        await Expect(page.GetByTestId("sample-compliance")).ToHaveTextAsync("sample");
    }

    [Fact]
    public async Task An_incompatible_extension_is_skipped_and_the_panel_works()
    {
        var logs = new CapturingLogs();
        await using var gateway = await SampleExtensionHost.StartAsync(
            prepare: folder => SampleExtensionHost.RewriteManifest(Path.Combine(folder, "sample"), "\"api\": \"1.1\"", "\"api\": \"2.0\""),
            configure: builder => builder.Logging.AddProvider(logs));

        var rejected = Assert.Single(gateway.App.Services.GetRequiredService<ExtensionCatalog>().Rejected);

        Assert.Equal("sample", rejected.Name);
        Assert.Contains(logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal) && line.Contains("sample", StringComparison.Ordinal));
        await AssertPanelWorksWithoutSampleAsync(gateway);
    }

    [Fact]
    public async Task An_extension_claiming_a_panel_route_is_skipped()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(prepare: folder =>
        {
            Directory.Move(Path.Combine(folder, "sample"), Path.Combine(folder, "other"));
            SampleExtensionHost.RewriteManifest(Path.Combine(folder, "other"), "\"id\": \"sample\"", "\"id\": \"other\"");
        });

        var rejected = Assert.Single(gateway.App.Services.GetRequiredService<ExtensionCatalog>().Rejected);

        Assert.Contains("/sample", rejected.Reason, StringComparison.Ordinal);
        await AssertPanelWorksWithoutSampleAsync(gateway);
    }

    [Fact]
    public async Task A_folder_whose_name_differs_from_the_id_is_skipped()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(
            prepare: folder => Directory.Move(Path.Combine(folder, "sample"), Path.Combine(folder, "renamed")));

        var rejected = Assert.Single(gateway.App.Services.GetRequiredService<ExtensionCatalog>().Rejected);

        Assert.Equal("renamed", rejected.Name);
        await AssertPanelWorksWithoutSampleAsync(gateway);
    }

    [Fact]
    public async Task A_folder_with_a_broken_assembly_is_skipped()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(
            prepare: folder => File.WriteAllText(Path.Combine(folder, "sample", "SampleExtension.dll"), "not an assembly"));

        Assert.Single(gateway.App.Services.GetRequiredService<ExtensionCatalog>().Rejected);
        await AssertPanelWorksWithoutSampleAsync(gateway);
    }

    [Fact]
    public async Task A_disabled_extension_is_not_loaded()
    {
        var logs = new CapturingLogs();
        await using var gateway = await SampleExtensionHost.StartAsync(
            new Dictionary<string, string> { ["CtxUi:DisabledExtensions"] = "sample" },
            configure: builder => builder.Logging.AddProvider(logs));

        var catalog = gateway.App.Services.GetRequiredService<ExtensionCatalog>();

        Assert.Empty(catalog.Loaded);
        Assert.Equal("sample", Assert.Single(catalog.Disabled));
        Assert.Empty(catalog.Rejected);
        Assert.DoesNotContain(logs.Lines, line => line.StartsWith("Error", StringComparison.Ordinal));
        await AssertPanelWorksWithoutSampleAsync(gateway);
    }

    [Fact]
    public async Task A_page_without_a_section_opens_by_its_address()
    {
        await using var gateway = await SampleExtensionHost.StartAsync(
            new Dictionary<string, string> { ["CtxUi:Extensions:sample:Headless"] = "true" });
        await using var session = await PanelSession.SignInAsync(gateway);
        Assert.Equal(BuiltIn, await LinksAsync(session.Page));

        await session.Page.GotoAsync("sample");

        await Expect(session.Page.GetByTestId("sample-note")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_throwing_page_stays_inside_its_section()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        await gateway.AddInboundAsync("echo", "http://127.0.0.1:1/mcp");
        await gateway.AddClientAsync("Иванов", ["echo"]);
        await using var session = await PanelSession.SignInAsync(gateway);

        await session.Page.GotoAsync("sample/boom");
        var page = await ClientPages.OpenClientsAsync(session);

        await Expect(ClientPages.Row(page, "Иванов")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_throwing_register_stops_the_start_and_names_the_extension()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => SampleExtensionHost.StartAsync(
            new Dictionary<string, string> { ["CtxUi:Extensions:sample:Fail"] = "true" }));

        Assert.Contains("sample", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CtxUi__DisabledExtensions", failure.Message, StringComparison.Ordinal);
    }
}
