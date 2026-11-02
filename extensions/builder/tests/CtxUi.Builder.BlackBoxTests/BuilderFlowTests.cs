using CtxUi.Extensions.Testing;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace CtxUi.Builder.BlackBoxTests;

public sealed class BuilderFlowTests(RunnerFixture runner) : IClassFixture<RunnerFixture>
{
    private const string Client = "Иванов";
    private const string WeatherSlug = "weather";

    [Fact(Skip = BuilderEnvironment.Requirements, SkipUnless = nameof(BuilderEnvironment.Ready), SkipType = typeof(BuilderEnvironment))]
    public async Task Server_goes_from_a_form_to_a_client_call_and_back()
    {
        await using var session = await BuilderSession.StartAsync(runner.Stack);
        await session.AddClientAsync(Client);
        await CreateWeatherServerAsync(session);

        await session.PublishAsync("Running, v1");
        await session.ConnectWithMistakeAsync("Not a slug", "Погода", Client);
        await session.ConnectAsync(WeatherSlug, Client);

        Assert.Contains("fastmcp", await session.CallThroughGatewayAsync(WeatherSlug, Client, "pkg_info", new { name = "fastmcp" }));

        await session.AddCodeToolAsync("broken");
        await session.ReplaceCodeAsync("def broken(:\n");
        await session.PublishExpectingFailureAsync(2);

        await session.Page.GetByTestId("open-releases").ClickAsync();
        await Expect(session.PublishState).ToHaveTextAsync("Running, v1");
        var details = await session.ServerDetailsAsync();
        Assert.Equal(1, details.GetProperty("activeRelease").GetInt32());
        Assert.Equal("Running", details.GetProperty("state").GetString());
        Assert.Contains("fastmcp", await session.CallThroughGatewayAsync(WeatherSlug, Client, "pkg_info", new { name = "fastmcp" }));
        Assert.DoesNotContain("fail:", session.Panel.Log, StringComparison.Ordinal);
    }

    [Fact(Skip = BuilderEnvironment.Requirements, SkipUnless = nameof(BuilderEnvironment.Ready), SkipType = typeof(BuilderEnvironment))]
    public async Task Python_tool_edit_republishes_without_installing_packages()
    {
        await using var session = await BuilderSession.StartAsync(runner.Stack);
        await session.AddClientAsync(Client);
        await session.CreateServerAsync("Greeter", "greeter");
        await session.AddCodeToolAsync("greet");

        await session.PublishAsync("Running, v1");
        await session.ConnectAsync("greeter", Client);
        Assert.Equal("Hello, Anna!", await session.CallThroughGatewayAsync("greeter", Client, "greet", new { name = "Anna" }));

        await session.Page.GetByTestId("open-releases").ClickAsync();
        await session.Page.Locator("[data-path='mcp/greet.py']").ClickAsync();
        await session.ReplaceCodeAsync("from fastmcp.tools import tool\n\n\n@tool\ndef greet(name: str) -> str:\n    return f\"Bonjour, {name}!\"\n");
        await session.PublishAsync("Running, v2");

        Assert.Equal("Bonjour, Anna!", await session.CallThroughGatewayAsync("greeter", Client, "greet", new { name = "Anna" }));
        await session.Page.GetByTestId("open-releases").ClickAsync();
        var second = session.Page.Locator("[data-release='2']");
        await Expect(second).ToContainTextAsync("Active");
        Assert.Contains("is already built, reusing it", await second.Locator("pre").TextContentAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("Installing the dependencies", await second.Locator("pre").TextContentAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("fail:", session.Panel.Log, StringComparison.Ordinal);
    }

    [Fact(Skip = "Set CTXUI_BINARY to a ctx-ui server binary to run the builder flow tests", SkipUnless = nameof(BuilderEnvironment.PanelBinaryIsSet), SkipType = typeof(BuilderEnvironment))]
    public async Task Panel_without_the_extension_shows_no_trace_of_it()
    {
        var empty = Directory.CreateTempSubdirectory("ctxb-no-extensions-").FullName;
        try
        {
            await using var panel = await TestPanel.StartAsync(new TestPanelOptions(empty, Language: "en"));
            await using var session = await BuilderSession.OpenAsync(panel);

            var overview = await session.Page.ContentAsync();
            Assert.DoesNotContain("builder", overview, StringComparison.OrdinalIgnoreCase);
            await Expect(session.Page.GetByRole(AriaRole.Navigation).GetByRole(AriaRole.Link, new() { Name = "Servers", Exact = true })).ToHaveCountAsync(0);

            var unknown = await session.Page.APIRequest.GetAsync(panel.Url + "nothing-here");
            var page = await session.Page.APIRequest.GetAsync(panel.Url + "builder");
            Assert.Equal(unknown.Status, page.Status);
            Assert.DoesNotContain("builder", await page.TextAsync(), StringComparison.OrdinalIgnoreCase);
            var unknownEndpoint = await session.Page.APIRequest.GetAsync(panel.Url + "extensions/nothing-here/servers");
            var endpoint = await session.Page.APIRequest.GetAsync(panel.Url + "extensions/builder/servers");
            Assert.Equal(unknownEndpoint.Status, endpoint.Status);
            Assert.Equal(unknownEndpoint.Headers["content-type"], endpoint.Headers["content-type"]);
            Assert.DoesNotContain("builder", await panel.CliAsync("extension", "list"), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fail:", panel.Log, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact(Skip = BuilderEnvironment.Requirements, SkipUnless = nameof(BuilderEnvironment.Ready), SkipType = typeof(BuilderEnvironment))]
    public async Task Exported_project_runs_with_plain_fastmcp()
    {
        await using var session = await BuilderSession.StartAsync(runner.Stack);
        await CreateWeatherServerAsync(session);

        var archive = await session.ExportAsync();
        await using var exported = new ExportedProject();
        await exported.RunAsync(archive, "https://pypi.org");

        string answer;
        try
        {
            answer = await exported.CallAsync("pkg_info", new Dictionary<string, object?> { ["name"] = "fastmcp" });
        }
        catch
        {
            Assert.Fail(await exported.LogAsync());
            throw;
        }

        Assert.Contains("fastmcp", answer);
    }

    private static async Task CreateWeatherServerAsync(BuilderSession session)
    {
        await session.CreateServerAsync("Weather", WeatherSlug);
        await session.SetBaseUrlAsync("https://pypi.org");
        await session.AddFormToolAsync("pkg_info", "GET", "/pypi/{name}/json", "Package metadata from PyPI", "name");
    }
}
