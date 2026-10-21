using System.Net;
using Microsoft.Extensions.Configuration;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public class HealthTests
{
    private static IConfiguration Listening(string urls) =>
        new ConfigurationBuilder().AddInMemoryCollection([KeyValuePair.Create<string, string?>("Urls", urls)]).Build();

    [Fact]
    public async Task Health_endpoint_answers_without_signing_in()
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_endpoint_reveals_nothing_else()
    {
        await using var gateway = await GatewayHost.StartAsync();

        using var response = await gateway.Http.GetAsync("/healthz/x");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Probe_reports_a_running_service_as_healthy()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var error = new StringWriter();

        var code = await HealthProbe.RunAsync(Listening(gateway.BaseUrl), error);

        Assert.Equal((0, ""), (code, error.ToString()));
    }

    [Fact]
    public async Task Probe_reports_a_service_that_does_not_answer_and_says_where_it_asked()
    {
        var error = new StringWriter();

        var code = await HealthProbe.RunAsync(Listening("http://127.0.0.1:1"), error);

        Assert.Equal(1, code);
        Assert.Contains("http://127.0.0.1:1/healthz", error.ToString());
    }
}
