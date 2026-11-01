using System.Net;

namespace CtxUi.IntegrationTests;

public class ExtensionServicesTests
{
    private static Task<GatewayHost> StartAsync(string flag) =>
        SampleExtensionHost.StartAsync(new Dictionary<string, string> { [$"CtxUi:Extensions:sample:{flag}"] = "true" });

    [Fact]
    public async Task An_endpoint_of_an_extension_needs_an_administrator_or_an_api_key()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();
        var admin = await gateway.AddAdminAsync("root");
        using var anonymous = ApiClient.WithKey(gateway, null);
        using var withKey = await ApiClient.IssueAsync(gateway);
        using var panel = new PanelClient(gateway);
        using var signedIn = await panel.SignInAsync("root", admin.Password);
        using var withCookie = new HttpClient(new HttpClientHandler { CookieContainer = panel.Cookies }) { BaseAddress = new Uri(gateway.PanelUrl) };

        using var closed = await anonymous.GetAsync("extensions/sample/ping");
        using var byKey = await withKey.GetAsync("extensions/sample/ping");
        using var byCookie = await withCookie.GetAsync("extensions/sample/ping");

        Assert.NotEqual(HttpStatusCode.OK, closed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byKey.StatusCode);
        Assert.Equal("pong", await byKey.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, byCookie.StatusCode);
    }

    [Fact]
    public async Task An_endpoint_stays_behind_the_secret_panel_path()
    {
        await using var gateway = await SampleExtensionHost.StartAsync();

        using var response = await gateway.Http.GetAsync("/extensions/sample/ping");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_extension_database_is_migrated_at_start_and_survives_a_restart()
    {
        await using var gateway = await StartAsync("Database");
        using (var api = await ApiClient.IssueAsync(gateway))
        using (var written = await api.PostAsync("extensions/sample/notes", "запись"))
            Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        await gateway.RestartAsync();

        using var again = await ApiClient.IssueAsync(gateway, "again");
        using var read = await again.GetAsync("extensions/sample/notes");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("запись", await read.Content.ReadAsStringAsync());
        Assert.True(File.Exists(Path.Combine(gateway.DataDir, "extension-data", "sample", "data.db")));
    }

    [Fact]
    public async Task A_hosted_service_of_an_extension_runs()
    {
        await using var gateway = await StartAsync("Hosted");
        using var api = await ApiClient.IssueAsync(gateway);

        await Eventually.TrueAsync(
            async () =>
            {
                using var response = await api.GetAsync("extensions/sample/hosted");
                return await response.Content.ReadAsStringAsync() == "started";
            },
            "фоновая служба расширения запустилась");
    }

    [Fact]
    public async Task A_throwing_initializer_stops_the_start_and_names_the_extension()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync("FailInitializer"));

        Assert.Contains("sample", failure.Message, StringComparison.Ordinal);
        Assert.Contains("CtxUi__DisabledExtensions", failure.Message, StringComparison.Ordinal);
    }
}
