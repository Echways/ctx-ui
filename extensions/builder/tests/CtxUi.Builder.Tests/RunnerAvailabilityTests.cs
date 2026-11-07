using System.Net;
using System.Net.Http.Json;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Runner;

namespace CtxUi.Builder.Tests;

public sealed class RunnerAvailabilityTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond());
        }
    }

    private static RunnerAvailability Create(RunnerSettings settings, Func<HttpResponseMessage> respond)
    {
        return new RunnerAvailability(settings,
            new RunnerClient(new HttpClient(new StubHandler(respond))
            { BaseAddress = new Uri("http://runner:8091/") }));
    }

    private static RunnerSettings Complete()
    {
        return new RunnerSettings("http://runner:8091", "http://runner:8090", "token");
    }

    [Fact]
    public async Task Missing_settings_are_named_and_no_request_is_sent()
    {
        var availability = Create(new RunnerSettings(null, "http://runner:8090", null),
            () => throw new InvalidOperationException());

        var report = await availability.CheckAsync(default);

        Assert.Equal(RunnerProblem.NotConfigured, report.Problem);
        Assert.Contains("RunnerUrl", report.Reason);
        Assert.Contains("RunnerToken", report.Reason);
        Assert.DoesNotContain("RunnerIngressUrl", report.Reason);
    }

    [Fact]
    public async Task Matching_version_is_ready()
    {
        var availability = Create(Complete(),
            () => JsonResponse(new RunnerHealth(RunnerAvailability.ExtensionVersion, "29.0", true)));

        var report = await availability.CheckAsync(default);

        Assert.Equal(RunnerProblem.None, report.Problem);
    }

    [Fact]
    public async Task Different_version_is_a_mismatch_naming_both_versions()
    {
        var availability = Create(Complete(), () => JsonResponse(new RunnerHealth("0.0.1", "29.0", true)));

        var report = await availability.CheckAsync(default);

        Assert.Equal(RunnerProblem.VersionMismatch, report.Problem);
        Assert.Contains("0.0.1", report.Reason);
        Assert.Contains(RunnerAvailability.ExtensionVersion, report.Reason);
    }

    [Fact]
    public async Task Refused_token_is_reported_without_the_token()
    {
        var availability = Create(Complete(), () => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var report = await availability.CheckAsync(default);

        Assert.Equal(RunnerProblem.TokenRefused, report.Problem);
        Assert.DoesNotContain("token\"", report.Reason);
    }

    [Fact]
    public async Task Unreachable_runner_is_reported_with_its_address()
    {
        var availability = Create(Complete(), () => throw new HttpRequestException("connection refused"));

        var report = await availability.CheckAsync(default);

        Assert.Equal(RunnerProblem.Unreachable, report.Problem);
        Assert.Contains("http://runner:8091", report.Reason);
    }

    [Fact]
    public void Relative_or_foreign_urls_count_as_missing()
    {
        var settings = new RunnerSettings("runner", "ftp://runner", "  ");

        Assert.Equal(["RunnerUrl", "RunnerIngressUrl", "RunnerToken"], settings.Missing);
    }

    [Fact]
    public void Settings_never_print_the_token()
    {
        Assert.DoesNotContain("secret-token", new RunnerSettings("http://a", "http://b", "secret-token").ToString());
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
