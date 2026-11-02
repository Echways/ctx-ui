using System.Security.Cryptography;
using System.Text.Json;
using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class PublishPipelineTests
{
    private static async Task<(PublishHarness Harness, Guid Server)> StartedAsync()
    {
        var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        return (harness, server);
    }

    [Fact]
    public async Task Successful_publish_saves_tools_lock_and_activates()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Inspector.Names = _ => ["alpha", "beta"];

        var number = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(1, number);
        Assert.Equal(ReleaseState.Active, release.Status);
        Assert.Equal("lock-1", release.LockFile);
        Assert.Equal("environment", release.EnvironmentHash);
        Assert.Contains("built 1", release.BuildLog);
        Assert.Equal(ProjectFiles.FastMcpVersion, release.FastMcpVersion);
        Assert.NotNull(release.FinishedAt);
        Assert.Null(release.Error);
        var names = JsonDocument.Parse(release.ToolsSnapshotJson!).RootElement.EnumerateArray().Select(tool => tool.GetProperty("name").GetString());
        Assert.Equal(["alpha", "beta"], names);
        Assert.Equal(release.Id, (await harness.ServerAsync(server)).ActiveReleaseId);
        Assert.Equal(1, harness.Runner.Active);
        Assert.Equal(["PUT server", "GET server", "PUT server", "POST release 1", "GET release 1", "PUT server", "POST release 1 start", "GET release 1", "POST release 1 activate"], harness.Runner.CallsSnapshot().Take(9));
        var stored = await File.ReadAllBytesAsync(harness.Archives.PathOf(release.Id));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(stored)), release.ArchiveHash);
        Assert.Equal(stored, harness.Runner.Archives[1]);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(harness.Archives.PathOf(release.Id))!, "*.tmp"));
    }

    [Fact]
    public async Task Build_failure_leaves_the_active_release_and_saves_the_log()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailBuilds.Add(2);

        var failed = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, failed);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Contains("no matching distribution", release.BuildLog);
        Assert.Equal("The dependency install failed.", release.Error);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Equal((await harness.ReleaseAsync(server, 1)).Id, (await harness.ServerAsync(server)).ActiveReleaseId);
        Assert.Equal(1, harness.Runner.Active);
        Assert.DoesNotContain("POST release 2 start", harness.Runner.CallsSnapshot());
    }

    [Fact]
    public async Task Start_timeout_leaves_the_active_release_and_marks_the_new_one_failed()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailStarts.Add(2);

        var failed = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, failed);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Contains("health check", release.Error);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Equal(1, harness.Runner.Active);
        Assert.DoesNotContain("POST release 2 activate", harness.Runner.CallsSnapshot());
    }

    [Fact]
    public async Task Removed_tools_are_reported_but_do_not_block_activation()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Inspector.Names = _ => ["alpha", "beta"];
        await harness.PublishAndWaitAsync(server);
        harness.Inspector.Names = _ => ["alpha"];

        var second = await harness.PublishAndWaitAsync(server);

        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, second)).Status);
        var views = await harness.WithScopeAsync(services => services.GetRequiredService<ReleaseCatalog>().ListAsync(server, default));
        Assert.Equal(["beta"], views.Single(view => view.Number == 2).RemovedTools);
        Assert.Empty(views.Single(view => view.Number == 1).RemovedTools);
        Assert.Equal([2, 1], views.Select(view => view.Number));
    }

    [Fact]
    public async Task Publish_during_a_build_is_refused_with_a_message()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.HoldBuilds = true;
        await harness.Queue.PublishAsync(server, PublishMode.Activate, default);

        var refused = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.PublishAsync(server, PublishMode.Activate, default));

        Assert.Equal(BuilderText.PublishBusy, refused.Message);
        Assert.Single(await harness.ReleasesAsync(server));
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.CallsSnapshot().Contains("POST release 1")));
        harness.Runner.HoldBuilds = false;
        harness.Runner.FinishBuild(1);
        await harness.WaitIdleAsync(server);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Equal(2, await harness.PublishAndWaitAsync(server));
    }

    [Fact]
    public async Task Publish_is_refused_while_the_runner_still_builds_an_unknown_release()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Runner.Releases[1] = new FakeRunner.Entry(1, ReleaseState.Building);
        await harness.StartAsync();

        var refused = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.PublishAsync(server, PublishMode.Activate, default));

        Assert.Equal(BuilderText.PublishBusy, refused.Message);
        Assert.Empty(await harness.ReleasesAsync(server));
        Assert.True(!harness.Queue.IsBusy(server));
    }

    [Fact]
    public async Task Interrupted_publish_is_marked_failed_on_start_and_next_publish_works()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<BuilderDatabase>();
            database.Releases.Add(new Release { Id = Guid.NewGuid(), ServerId = server, Number = 3, Status = ReleaseState.Building, CreatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync();
            return 0;
        });

        await harness.Queue.InitializeAsync(default);
        await harness.StartAsync();

        var interrupted = await harness.ReleaseAsync(server, 3);
        Assert.Equal(ReleaseState.Failed, interrupted.Status);
        Assert.Equal(BuilderText.PublishInterrupted, interrupted.Error);
        Assert.NotNull(interrupted.FinishedAt);
        Assert.Equal(4, await harness.PublishAndWaitAsync(server));
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 4)).Status);
    }

    [Fact]
    public async Task Runner_restart_during_a_build_fails_the_release_and_a_new_publish_works()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.HoldBuilds = true;
        var number = await harness.Queue.PublishAsync(server, PublishMode.Activate, default);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.CallsSnapshot().Contains("POST release 1")));

        harness.Runner.Forget(1);
        await harness.WaitIdleAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Equal(BuilderText.RunnerForgotRelease, release.Error);
        harness.Runner.HoldBuilds = false;
        Assert.Equal(2, await harness.PublishAndWaitAsync(server));
    }

    [Fact]
    public async Task Runner_restored_failed_status_after_restart_fails_the_release()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.HoldBuilds = true;
        var number = await harness.Queue.PublishAsync(server, PublishMode.Activate, default);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.CallsSnapshot().Contains("POST release 1")));

        harness.Runner.Restart();
        await harness.WaitIdleAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Contains("restart of the runner", release.Error);
        harness.Runner.HoldBuilds = false;
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, await harness.PublishAndWaitAsync(server))).Status);
    }

    [Fact]
    public async Task Runner_outage_during_a_build_fails_the_release_after_the_tolerance()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.HoldBuilds = true;
        var number = await harness.Queue.PublishAsync(server, PublishMode.Activate, default);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.CallsSnapshot().Contains("POST release 1")));

        harness.Runner.Unreachable = true;
        await harness.WaitIdleAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Equal(string.Format(BuilderText.RunnerLostDuringBuild, 1), release.Error);
    }

    [Fact]
    public async Task Rollback_with_live_volumes_activates_without_a_build()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        await harness.PublishAndWaitAsync(server);
        var builds = harness.Runner.CallsSnapshot().Count(call => call == "POST release 1");

        await harness.Queue.RollbackAsync(server, 1, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(builds, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
        Assert.Equal(1, harness.Runner.Active);
        var first = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Active, first.Status);
        Assert.Equal(ReleaseState.Retired, (await harness.ReleaseAsync(server, 2)).Status);
        Assert.Equal(first.Id, (await harness.ServerAsync(server)).ActiveReleaseId);
        Assert.Equal(2, (await harness.ReleasesAsync(server)).Count);
    }

    [Fact]
    public async Task Rollback_without_volumes_rebuilds_from_the_snapshot()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        await harness.PublishAndWaitAsync(server);
        var original = harness.Runner.Archives[1];
        harness.Runner.Forget(1);

        await harness.Queue.RollbackAsync(server, 1, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(2, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
        Assert.Equal(original, harness.Runner.Archives[1]);
        Assert.Equal(1, harness.Runner.Active);
        var first = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Active, first.Status);
        Assert.Equal("lock-1", first.LockFile);
    }

    [Fact]
    public async Task Rollback_refuses_a_corrupted_snapshot_and_keeps_the_active_release()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        await harness.PublishAndWaitAsync(server);
        var first = await harness.ReleaseAsync(server, 1);
        await File.WriteAllBytesAsync(harness.Archives.PathOf(first.Id), [1, 2, 3]);
        harness.Runner.Forget(1);

        await harness.Queue.RollbackAsync(server, 1, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(2, harness.Runner.Active);
        var after = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Retired, after.Status);
        Assert.Equal(BuilderText.ArchiveCorrupted, after.Error);
        Assert.Equal(1, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
    }

    [Fact]
    public async Task Rollback_keeps_stored_values_when_the_runner_restored_empty_ones()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Restart();

        await harness.Queue.RollbackAsync(server, 1, default);
        await harness.WaitIdleAsync(server);

        var first = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Active, first.Status);
        Assert.Equal("lock-1", first.LockFile);
        Assert.Contains("built 1", first.BuildLog);
    }

    [Fact]
    public async Task Rollback_to_a_failed_or_active_release_is_refused()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailBuilds.Add(2);
        await harness.PublishAndWaitAsync(server);

        await Assert.ThrowsAsync<PanelException>(() => harness.Queue.RollbackAsync(server, 1, default));
        await Assert.ThrowsAsync<PanelException>(() => harness.Queue.RollbackAsync(server, 2, default));
        await Assert.ThrowsAsync<PanelException>(() => harness.Queue.RollbackAsync(server, 9, default));
        Assert.False(harness.Queue.IsBusy(server));
    }

    [Fact]
    public async Task Changing_secrets_or_limits_restarts_through_candidate_and_activate()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        var builds = harness.Runner.CallsSnapshot().Count(call => call == "POST release 1");
        await harness.WithScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<ServerCatalog>();
            await catalog.SetSecretAsync(server, "API_KEY", "s3cret", default);
            await catalog.SetAccessAsync(server, NetworkAccess.FullNetwork, new ServerLimits(768, 2, 128), default);
            return 0;
        });
        var startsBefore = harness.Runner.Starts.Count;

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(builds, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
        var spec = harness.Runner.Specs[^1];
        Assert.Equal(NetworkAccess.FullNetwork, spec.Network);
        Assert.Equal(new ServerLimits(768, 2, 128), spec.Limits);
        Assert.Equal(startsBefore + 1, harness.Runner.Starts.Count);
        Assert.Equal("s3cret", harness.Runner.Starts[^1].Start.Environment["API_KEY"]);
        Assert.Equal(1, harness.Runner.Starts[^1].Number);
        Assert.Equal("POST release 1 activate", harness.Runner.CallsSnapshot()[^1]);
        var release = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Active, release.Status);
        Assert.Null(release.Error);
        Assert.Equal(release.Id, (await harness.ServerAsync(server)).ActiveReleaseId);
    }

    [Fact]
    public async Task Failed_settings_apply_keeps_the_active_release_and_reports_the_error()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailStarts.Add(1);

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitIdleAsync(server);

        var release = await harness.ReleaseAsync(server, 1);
        Assert.Equal(ReleaseState.Active, release.Status);
        Assert.Contains("health check", release.Error);
        Assert.Equal(release.Id, (await harness.ServerAsync(server)).ActiveReleaseId);
        Assert.Equal(1, harness.Runner.Active);
    }

    [Fact]
    public async Task Applying_settings_without_an_active_release_only_saves()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitIdleAsync(server);

        Assert.Empty(harness.Runner.CallsSnapshot());
    }

    [Fact]
    public async Task Settings_changed_during_a_build_are_applied_after_it()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.HoldBuilds = true;
        await harness.Queue.PublishAsync(server, PublishMode.Activate, default);

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.CallsSnapshot().Count(call => call == "POST release 2") == 1));
        var startsBefore = harness.Runner.Starts.Count;
        harness.Runner.HoldBuilds = false;
        harness.Runner.FinishBuild(2);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.Starts.Count >= startsBefore + 2));
        await harness.WaitIdleAsync(server);

        Assert.Equal(2, harness.Runner.Active);
    }

    [Fact]
    public async Task Build_only_stops_at_a_candidate_without_activation()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);

        var number = await harness.PublishAndWaitAsync(server, PublishMode.BuildOnly);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Candidate, release.Status);
        Assert.NotNull(release.ToolsSnapshotJson);
        Assert.Equal(1, harness.Runner.Active);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Contains(2, harness.Inspector.Listed);

        await harness.Queue.RollbackAsync(server, 2, default);
        await harness.WaitIdleAsync(server);
        Assert.Equal(2, harness.Runner.Active);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 2)).Status);
    }

    [Fact]
    public async Task Check_timeout_marks_the_release_failed_and_the_active_one_continues()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Inspector.Hang = true;

        var number = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Equal(string.Format(BuilderText.CheckTimedOut, 1), release.Error);
        Assert.Equal(1, harness.Runner.Active);
    }

    [Fact]
    public async Task Failing_check_marks_the_release_failed_with_the_reason()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Inspector.Failure = new InvalidOperationException("tools/list refused");

        var number = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Equal(string.Format(BuilderText.CheckFailed, "tools/list refused"), release.Error);
        Assert.Null(harness.Runner.Active);
    }

    [Fact]
    public async Task Failed_activation_leaves_the_old_release_active()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.FailActivations.Add(2);

        var number = await harness.PublishAndWaitAsync(server);

        var release = await harness.ReleaseAsync(server, number);
        Assert.Equal(ReleaseState.Failed, release.Status);
        Assert.Contains("not a healthy candidate", release.Error);
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
        Assert.Equal((await harness.ReleaseAsync(server, 1)).Id, (await harness.ServerAsync(server)).ActiveReleaseId);
    }

    [Fact]
    public async Task Lost_specification_is_sent_again_and_the_build_is_retried_once()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.DropSpecOnce = true;

        var number = await harness.PublishAndWaitAsync(server);

        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, number)).Status);
        Assert.Equal(2, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
    }

    [Fact]
    public async Task Release_numbers_keep_increasing_after_failures_and_runner_leftovers()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        harness.Runner.FailBuilds.Add(1);
        Assert.Equal(1, await harness.PublishAndWaitAsync(server));
        harness.Runner.SeedBuilt(5);

        Assert.Equal(6, await harness.PublishAndWaitAsync(server));
        Assert.Equal(7, await harness.PublishAndWaitAsync(server));
        Assert.Equal([1, 6, 7], (await harness.ReleasesAsync(server)).Select(release => release.Number));
    }

    [Fact]
    public async Task Lost_activation_marker_is_repaired_at_start_by_activating_the_stored_release()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Restart(loseMarker: true);
        Assert.Null(harness.Runner.Active);
        var builds = harness.Runner.CallsSnapshot().Count(call => call == "POST release 1");

        await harness.RestartPanelAsync();

        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.Active == 1));
        Assert.Equal(builds, harness.Runner.CallsSnapshot().Count(call => call == "POST release 1"));
        Assert.Single(await harness.ReleasesAsync(server));
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
    }

    [Fact]
    public async Task Lost_marker_with_a_stopped_container_is_reported_and_not_rebuilt()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Restart(loseMarker: true);
        harness.Runner.Releases[1].State = ReleaseState.Retired;
        var calls = harness.Runner.CallsSnapshot().Length;

        await harness.RestartPanelAsync();
        await harness.WaitIdleAsync(server);

        Assert.Null(harness.Runner.Active);
        Assert.DoesNotContain("POST release 1 activate", harness.Runner.CallsSnapshot().Skip(calls));
        Assert.DoesNotContain("POST release 1", harness.Runner.CallsSnapshot().Skip(calls));
        var status = await harness.WithScopeAsync(services => services.GetRequiredService<ReleaseCatalog>().StatusAsync(server, default));
        Assert.Equal(ServerRunState.NeedsReactivation, status.State);
        Assert.Equal(1, status.ActiveNumber);
    }

    [Fact]
    public async Task Publish_without_runner_settings_says_what_is_missing()
    {
        await using var harness = await PublishHarness.CreateAsync(configured: false);
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();

        var refused = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.PublishAsync(server, PublishMode.Activate, default));

        Assert.Equal(string.Format(BuilderText.RunnerNotConfigured, "RunnerUrl, RunnerIngressUrl, RunnerToken"), refused.Message);
        Assert.Empty(await harness.ReleasesAsync(server));
    }

    [Fact]
    public async Task Publish_of_an_api_project_without_a_base_url_is_refused_before_a_snapshot()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.WithScopeAsync(async services =>
        {
            var database = services.GetRequiredService<BuilderDatabase>();
            await database.Drafts.Where(draft => draft.ServerId == server).ExecuteUpdateAsync(setters => setters.SetProperty(draft => draft.OpenApiJson, "{}"));
            return 0;
        });

        var refused = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.PublishAsync(server, PublishMode.Activate, default));

        Assert.Equal(BuilderText.PublishBaseUrlRequired, refused.Message);
        Assert.Empty(await harness.ReleasesAsync(server));
        Assert.False(harness.Queue.IsBusy(server));
    }

    [Fact]
    public async Task Repair_waits_for_a_runner_that_is_unreachable_at_start()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await harness.PublishAndWaitAsync(server);
        harness.Runner.Restart(loseMarker: true);
        harness.Runner.Unreachable = true;
        harness.RepairInterval = TimeSpan.FromMilliseconds(40);

        await harness.RestartPanelAsync();
        await Task.Delay(150);
        Assert.Null(harness.Runner.Active);
        harness.Runner.Unreachable = false;

        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.Active == 1));
        Assert.Equal(ReleaseState.Active, (await harness.ReleaseAsync(server, 1)).Status);
    }

    [Fact]
    public async Task Repair_notices_a_runner_restart_while_the_panel_keeps_running()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.RepairInterval = TimeSpan.FromMilliseconds(40);
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        Assert.Equal(1, harness.Runner.Active);

        harness.Runner.Restart(loseMarker: true);

        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.Active == 1));
        Assert.Single(await harness.ReleasesAsync(server));
    }

    [Fact]
    public async Task A_changed_api_credential_restarts_through_candidate_and_activate_with_the_new_value()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await SetApiAsync(harness, server, "https://api.example.com", "old");
        await harness.PublishAndWaitAsync(server);
        var startsBefore = harness.Runner.Starts.Count;
        await SetApiAsync(harness, server, "https://api.example.com", "rotated");

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(startsBefore + 1, harness.Runner.Starts.Count);
        Assert.Contains("Bearer rotated", harness.Runner.Starts[^1].Start.Environment["BUILDER_API_HEADERS"]);
        Assert.Equal("POST release 1 activate", harness.Runner.CallsSnapshot()[^1]);
    }

    [Fact]
    public async Task A_draft_base_url_is_not_used_by_a_restart()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await SetApiAsync(harness, server, "https://live.example.com", "token");
        await harness.PublishAndWaitAsync(server);
        await SetApiAsync(harness, server, "https://draft.example.com", "token");

        await harness.Queue.ApplySettingsAsync(server, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal("https://live.example.com", harness.Runner.Starts[^1].Start.Environment["BUILDER_BASE_URL"]);
    }

    [Fact]
    public async Task Rollback_starts_the_old_release_with_its_own_base_url()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        await SetApiAsync(harness, server, "https://first.example.com", "token");
        await harness.PublishAndWaitAsync(server);
        await SetApiAsync(harness, server, "https://second.example.com", "token");
        await harness.PublishAndWaitAsync(server);
        await SetApiAsync(harness, server, "https://draft.example.com", "token");

        await harness.Queue.RollbackAsync(server, 1, default);
        await harness.WaitIdleAsync(server);

        Assert.Equal(1, harness.Runner.Starts[^1].Number);
        Assert.Equal("https://first.example.com", harness.Runner.Starts[^1].Start.Environment["BUILDER_BASE_URL"]);
        Assert.Equal("https://first.example.com", (await harness.ReleaseAsync(server, 1)).BaseUrl);
        Assert.Equal("https://second.example.com", (await harness.ReleaseAsync(server, 2)).BaseUrl);
    }

    private static Task SetApiAsync(PublishHarness harness, Guid server, string baseUrl, string token) =>
        harness.WithScopeAsync(async services =>
        {
            var catalog = services.GetRequiredService<ServerCatalog>();
            var revision = (await catalog.SettingsAsync(server, default)).SettingsRevision;
            await catalog.SetApiAsync(server, baseUrl, new ApiAuthInput(ApiAuthKind.Bearer, Secret: token), revision, default);
            return 0;
        });

    [Fact]
    public async Task Changed_is_raised_while_a_publish_progresses()
    {
        var (harness, server) = await StartedAsync();
        await using var _ = harness;
        var raised = new List<Guid>();
        harness.Queue.Changed += raised.Add;

        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);

        Assert.NotEmpty(raised);
        Assert.All(raised, id => Assert.Equal(server, id));
    }
}
