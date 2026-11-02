using System.Net.Http;
using System.Text.Json.Nodes;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace CtxUi.Builder.Tests;

public sealed class ToolTestRunnerTests
{
    private static ToolTestRunner RunnerOf(PublishHarness harness) => harness.Services.GetRequiredService<ToolTestRunner>();

    [Fact]
    public async Task Server_without_any_release_has_nothing_to_test()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();

        Assert.Null(await RunnerOf(harness).TargetAsync(server, default));
    }

    [Fact]
    public async Task Active_release_is_tested_through_the_active_route()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);

        var target = await RunnerOf(harness).TargetAsync(server, default);
        await RunnerOf(harness).CallAsync(server, target!, "hello", new JsonObject(), default);

        Assert.Equal(new ToolTarget(1, null, false), target);
        Assert.Equal([(int?)null], harness.Inspector.Called);
    }

    [Fact]
    public async Task Built_candidate_is_tested_before_the_active_release()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        var candidate = await harness.PublishAndWaitAsync(server, PublishMode.BuildOnly);
        harness.Inspector.Listed.Clear();

        var target = await RunnerOf(harness).TargetAsync(server, default);
        await RunnerOf(harness).ListAsync(server, target!, default);
        await RunnerOf(harness).CallAsync(server, target!, "hello", new JsonObject(), default);

        Assert.Equal(new ToolTarget(candidate, candidate, true), target);
        Assert.Equal([(int?)candidate], harness.Inspector.Called);
        Assert.Equal([(int?)candidate], harness.Inspector.Listed);
    }

    [Fact]
    public async Task Activated_candidate_stops_being_the_target()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server, PublishMode.BuildOnly);
        var active = await harness.PublishAndWaitAsync(server);

        Assert.Equal(new ToolTarget(active, null, false), await RunnerOf(harness).TargetAsync(server, default));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Transport_failures_become_a_panel_message(Exception failure)
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Inspector.Failure = failure;
        var target = new ToolTarget(1, null, false);

        var listed = await Assert.ThrowsAsync<PanelException>(() => RunnerOf(harness).ListAsync(server, target, default));
        var called = await Assert.ThrowsAsync<PanelException>(() => RunnerOf(harness).CallAsync(server, target, "hello", new JsonObject(), default));

        Assert.Contains(failure.Message, listed.Message);
        Assert.Contains(failure.Message, called.Message);
    }

    public static TheoryData<Exception> Failures() =>
    [
        new HttpRequestException("connection refused"),
        new IOException("stream closed"),
        new McpException("session lost"),
    ];

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_failure()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Inspector.Failure = new OperationCanceledException();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunnerOf(harness).CallAsync(server, new ToolTarget(1, null, false), "hello", new JsonObject(), cancelled.Token));
    }
}
