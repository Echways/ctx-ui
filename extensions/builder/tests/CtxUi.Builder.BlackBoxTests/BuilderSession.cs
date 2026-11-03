using System.Text.Json;
using System.Text.RegularExpressions;
using CtxUi.Extensions.Testing;
using Microsoft.Playwright;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.Builder.BlackBoxTests;

internal sealed partial class BuilderSession : IAsyncDisposable
{
    private static readonly TimeSpan BuildPatience = TimeSpan.FromMinutes(6);

    private readonly RunnerStack? _stack;
    private readonly bool _ownsPanel;
    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly Dictionary<string, string> _tokens = [];

    private BuilderSession(TestPanel panel, bool ownsPanel, RunnerStack? stack, IPlaywright playwright, IBrowser browser, IPage page)
    {
        Panel = panel;
        _ownsPanel = ownsPanel;
        _stack = stack;
        _playwright = playwright;
        _browser = browser;
        Page = page;
    }

    public TestPanel Panel { get; }

    public IPage Page { get; }

    public Guid ServerId { get; private set; }

    public static async Task<BuilderSession> StartAsync(RunnerStack stack)
    {
        await stack.EnsureStartedAsync();
        var panel = await TestPanel.StartAsync(new TestPanelOptions(Path.Combine(AppContext.BaseDirectory, "extensions"), stack.PanelSettings, "en"));
        return await OpenAsync(panel, stack, ownsPanel: true);
    }

    public static async Task<BuilderSession> OpenAsync(TestPanel panel, RunnerStack? stack = null, bool ownsPanel = false)
    {
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync();
        var page = await browser.NewPageAsync();
        await panel.SignInAsync(page);
        return new BuilderSession(panel, ownsPanel, stack, playwright, browser, page);
    }

    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        if (_stack is not null)
            await _stack.RemoveServersAsync();
        if (_ownsPanel)
            await Panel.DisposeAsync();
    }

    public ILocator Dialog => Page.GetByRole(AriaRole.Dialog);

    public ILocator PublishState => Page.GetByTestId("publish-state");

    public async Task CreateServerAsync(string name, string slug)
    {
        await Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Servers", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "New server" }).ClickAsync();
        await Dialog.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
        await Dialog.GetByLabel("Slug", new() { Exact = true }).FillAsync(slug);
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
        await Page.GetByRole(AriaRole.Row).Filter(new() { HasText = slug }).GetByText(name, new() { Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(ServerAddress());
        ServerId = Guid.Parse(ServerAddress().Match(Page.Url).Groups[1].Value);
        _stack?.Track(ServerId);
    }

    public async Task SetBaseUrlAsync(string baseUrl)
    {
        await Page.GetByTestId("open-settings").ClickAsync();
        await Page.GetByLabel("Base address", new() { Exact = true }).FillAsync(baseUrl);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync();
        await Expect(Page.GetByTestId("base-url-pending")).ToBeVisibleAsync();
    }

    public async Task AddFormToolAsync(string name, string method, string path, string description, string pathParameter)
    {
        await Page.GetByTestId("new-form-tool").ClickAsync();
        var form = Page.GetByTestId("tool-form");
        await form.GetByLabel("Tool name", new() { Exact = true }).FillAsync(name);
        await form.GetByLabel("Method", new() { Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = method, Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Listbox)).ToBeHiddenAsync();
        await Expect(form.GetByLabel("Method", new() { Exact = true })).ToHaveValueAsync(method);
        await form.GetByLabel("Path", new() { Exact = true }).First.FillAsync(path);
        await form.GetByLabel("Description", new() { Exact = true }).First.FillAsync(description);
        await form.GetByTestId("add-parameter").ClickAsync();
        var row = form.GetByTestId("parameter-row");
        await row.GetByLabel("Name", new() { Exact = true }).FillAsync(pathParameter);
        await row.GetByLabel("Where", new() { Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Option, new() { Name = "Path", Exact = true }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Listbox)).ToBeHiddenAsync();
        await form.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(Page.Locator($"[data-form-tool='{name}']")).ToBeVisibleAsync();
    }

    public async Task AddCodeToolAsync(string name)
    {
        await Page.GetByTestId("new-tool").ClickAsync();
        await Page.GetByTestId("new-tool-name").FillAsync(name);
        await Page.GetByTestId("add-tool").ClickAsync();
        await Expect(Page.GetByTestId("editor").Locator(".cm-content")).ToBeVisibleAsync();
    }

    public async Task ReplaceCodeAsync(string code)
    {
        var editor = Page.GetByTestId("editor").Locator(".cm-content");
        await editor.ClickAsync();
        await Page.Keyboard.PressAsync("ControlOrMeta+a");
        await Page.Keyboard.InsertTextAsync(code);
        await Expect(Page.GetByTestId("save-state")).ToHaveTextAsync("Saved", new() { Timeout = 15_000 });
    }

    public async Task PublishAsync(string expectedState)
    {
        await Page.GetByTestId("publish").ClickAsync();
        await Expect(PublishState).ToHaveTextAsync(expectedState, new() { Timeout = (float)BuildPatience.TotalMilliseconds });
    }

    public async Task PublishExpectingFailureAsync(int failedNumber)
    {
        await Page.GetByTestId("publish").ClickAsync();
        await Page.GetByTestId("open-releases").ClickAsync();
        var release = Page.Locator($"[data-release='{failedNumber}']");
        await Expect(release).ToContainTextAsync("Failed", new() { Timeout = (float)BuildPatience.TotalMilliseconds });
    }

    public async Task<string> AddClientAsync(string name)
    {
        await Panel.CliAsync("inbound", "add", "placeholder", "--url", "http://127.0.0.1:9/mcp");
        var output = await Panel.CliAsync("client", "add", name, "--inbound", "placeholder");
        _tokens[name] = TokenLine().Match(output).Groups[1].Value;
        return _tokens[name];
    }

    public async Task ConnectAsync(string slug, string client)
    {
        await Expect(Dialog).ToBeVisibleAsync();
        await Dialog.GetByLabel("Inbound slug", new() { Exact = true }).FillAsync(slug);
        await Dialog.GetByRole(AriaRole.Checkbox, new() { Name = client }).CheckAsync();
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();
        await Expect(Page.GetByTestId("connected")).ToBeVisibleAsync();
    }

    public async Task ConnectWithMistakeAsync(string slug, string name, string client)
    {
        await Expect(Dialog).ToBeVisibleAsync();
        await Dialog.GetByLabel("Inbound slug", new() { Exact = true }).FillAsync(slug);
        await Dialog.GetByLabel("Name", new() { Exact = true }).FillAsync(name);
        await Dialog.GetByRole(AriaRole.Checkbox, new() { Name = client }).CheckAsync();
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Connect", Exact = true }).ClickAsync();

        await Expect(Dialog.GetByRole(AriaRole.Alert)).ToBeVisibleAsync();
        await Expect(Dialog.GetByLabel("Inbound slug", new() { Exact = true })).ToHaveValueAsync(slug);
        await Expect(Dialog.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync(name);
        await Expect(Dialog.GetByRole(AriaRole.Checkbox, new() { Name = client })).ToBeCheckedAsync();
    }

    public async Task<JsonElement> ServerDetailsAsync()
    {
        var response = await Page.APIRequest.GetAsync($"{Panel.Url}extensions/builder/servers/{ServerId}");
        Assert.True(response.Ok, await response.TextAsync());
        return (await response.JsonAsync())!.Value;
    }

    public async Task<byte[]> ExportAsync()
    {
        var response = await Page.APIRequest.GetAsync($"{Panel.Url}extensions/builder/servers/{ServerId}/export");
        Assert.True(response.Ok, await response.TextAsync());
        return await response.BodyAsync();
    }

    public async Task<string> CallThroughGatewayAsync(string slug, string client, string tool, object arguments)
    {
        var endpoint = new Uri(new Uri(Panel.Url), $"/mcp/{slug}");
        var options = new HttpClientTransportOptions
        {
            Endpoint = endpoint,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + _tokens[client] },
        };
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(options));
        var result = await mcp.CallToolAsync(tool, JsonSerializer.SerializeToElement(arguments).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone()));
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    }

    [GeneratedRegex(@"/builder/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex ServerAddress();

    [GeneratedRegex(@"Token:\s+(\S+)")]
    private static partial Regex TokenLine();
}
