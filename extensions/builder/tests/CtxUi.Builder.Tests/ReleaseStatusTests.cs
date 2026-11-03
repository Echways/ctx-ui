using CtxUi.Builder.Contracts;
using CtxUi.Builder.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class ReleaseStatusTests
{
    private static Task<ServerStatus> StatusAsync(PublishHarness harness, Guid server) =>
        harness.WithScopeAsync(services => services.GetRequiredService<ReleaseCatalog>().StatusAsync(server, default));

    [Fact]
    public async Task A_server_without_releases_has_no_release_state()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();

        var status = await StatusAsync(harness, server);

        Assert.Equal(ServerRunState.NoRelease, status.State);
        Assert.Null(status.ActiveNumber);
    }

    [Fact]
    public async Task The_active_release_stays_the_true_version_while_the_runner_reports_it_failed_during_a_replacement()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Releases[1].State = ReleaseState.Failed;

        var status = await StatusAsync(harness, server);

        Assert.Equal(ServerRunState.Running, status.State);
        Assert.Equal(1, status.ActiveNumber);
    }

    [Fact]
    public async Task A_build_in_progress_is_shown_with_the_version_that_still_runs()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        harness.Runner.HoldBuilds = true;
        await harness.Queue.PublishAsync(server, PublishMode.Activate, default);

        var status = await StatusAsync(harness, server);

        Assert.Equal(ServerRunState.Building, status.State);
        Assert.Equal(1, status.ActiveNumber);
        Assert.Equal(2, status.BuildingNumber);
    }

    [Fact]
    public async Task An_unreachable_runner_is_reported_with_the_stored_version()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Unreachable = true;

        var status = await StatusAsync(harness, server);

        Assert.Equal(ServerRunState.RunnerUnavailable, status.State);
        Assert.Equal(1, status.ActiveNumber);
    }

    [Fact]
    public async Task Releases_are_listed_newest_first_with_their_log_and_error()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailBuilds.Add(2);
        await harness.PublishAndWaitAsync(server);

        var views = await harness.WithScopeAsync(services => services.GetRequiredService<ReleaseCatalog>().ListAsync(server, default));

        Assert.Equal([2, 1], views.Select(view => view.Number));
        Assert.True(views[1].IsActive);
        Assert.False(views[1].CanRollback);
        Assert.False(views[0].CanRollback);
        Assert.Equal("The dependency install failed.", views[0].Error);
        Assert.Contains("no matching distribution", views[0].BuildLog);
    }
}
