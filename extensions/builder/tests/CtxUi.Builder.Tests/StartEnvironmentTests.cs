using System.Text;
using System.Text.Json;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Servers;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class StartEnvironmentTests
{
    private static async Task<IReadOnlyDictionary<string, string>> StartedWithAsync(Func<ServerCatalog, Guid, Task> configure)
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.WithScopeAsync(async services =>
        {
            await configure(services.GetRequiredService<ServerCatalog>(), server);
            return 0;
        });
        await harness.PublishAndWaitAsync(server);
        return harness.Runner.Starts.Single().Start.Environment;
    }

    private static IReadOnlyDictionary<string, string> Headers(IReadOnlyDictionary<string, string> environment) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(environment["BUILDER_API_HEADERS"])!;

    [Fact]
    public async Task Without_api_settings_only_the_server_name_and_secrets_are_passed()
    {
        var environment = await StartedWithAsync((catalog, id) => catalog.SetSecretAsync(id, "TOKEN", "value", default));

        Assert.Equal("value", environment["TOKEN"]);
        Assert.Equal("weather", environment["BUILDER_SERVER_NAME"]);
        Assert.DoesNotContain("BUILDER_BASE_URL", environment.Keys);
        Assert.DoesNotContain("BUILDER_API_HEADERS", environment.Keys);
    }

    [Fact]
    public async Task Bearer_auth_becomes_an_authorization_header()
    {
        var environment = await StartedWithAsync((catalog, id) => catalog.SetApiAsync(id, "https://api.example.com/v1/", new ApiAuthInput(ApiAuthKind.Bearer, Secret: "abc"), 0, default));

        Assert.Equal("https://api.example.com/v1", environment["BUILDER_BASE_URL"]);
        Assert.Equal(new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }, Headers(environment));
    }

    [Fact]
    public async Task Header_auth_uses_the_named_header()
    {
        var environment = await StartedWithAsync((catalog, id) => catalog.SetApiAsync(id, "https://api.example.com", new ApiAuthInput(ApiAuthKind.Header, HeaderName: "X-Api-Key", Secret: "abc"), 0, default));

        Assert.Equal(new Dictionary<string, string> { ["X-Api-Key"] = "abc" }, Headers(environment));
    }

    [Fact]
    public async Task Basic_auth_encodes_user_and_secret()
    {
        var environment = await StartedWithAsync((catalog, id) => catalog.SetApiAsync(id, "https://api.example.com", new ApiAuthInput(ApiAuthKind.Basic, Username: "анна", Secret: "p:ss"), 0, default));

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("анна:p:ss"));
        Assert.Equal(new Dictionary<string, string> { ["Authorization"] = expected }, Headers(environment));
    }

    [Fact]
    public async Task A_user_secret_cannot_replace_the_builder_variables()
    {
        var environment = await StartedWithAsync((catalog, id) => catalog.SetSecretAsync(id, "BUILDER_SERVER_NAME", "evil", default));

        Assert.Equal("weather", environment["BUILDER_SERVER_NAME"]);
    }
}
