namespace CtxUi.Server.Tests;

public class HealthProbeTests
{
    [Theory]
    [InlineData("http://0.0.0.0:8080", "http://127.0.0.1:8080/healthz")]
    [InlineData("https://x:443;http://+:5000", "http://127.0.0.1:5000/healthz")]
    [InlineData("http://*:9000", "http://127.0.0.1:9000/healthz")]
    [InlineData("http://[::]:8080", "http://127.0.0.1:8080/healthz")]
    [InlineData("http://localhost:5000/", "http://localhost:5000/healthz")]
    [InlineData(null, "http://127.0.0.1:8080/healthz")]
    public void Probe_asks_the_first_plain_http_listener_on_this_machine(string? urls, string expected) =>
        Assert.Equal(expected, HealthProbe.Target(urls));
}
