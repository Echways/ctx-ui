using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CtxUi.Builder.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace CtxUi.Builder.Runner.Tests;

public sealed class ReleaseFlowTests
{
    private static readonly Guid Id = RunnerHarness.Id;
    private static readonly string Prefix = RunnerHarness.Prefix;

    private static string Server(int release) => $"{Prefix}-r{release}";

    private static string Code(int release) => $"{Prefix}-code-{release}";

    private static string Config(params string[] dependencies) =>
        JsonSerializer.Serialize(new { environment = new { python = "3.13", dependencies } });

    [Fact]
    public async Task Activating_keeps_the_route_on_the_old_release_until_the_new_one_is_healthy()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await using var one = await FakeUpstream.StartAsync("one");
        await using var two = await FakeUpstream.StartAsync("two");
        harness.Probe.Route(Server(1), one.Address);
        harness.Probe.Route(Server(2), two.Address);
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        Assert.Equal("one", await harness.CallActiveAsync());

        harness.Probe.SetHealthy(Server(2), false);
        await harness.BuildAsync(2);
        using var started = await harness.PostStartAsync(2);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(2))?.Running == true);

        using var tooEarly = await harness.ActivateAsync(2);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("one", await harness.CallActiveAsync());
        Assert.Equal(1, (await harness.StateAsync()).ActiveRelease);

        harness.Probe.SetHealthy(Server(2), true);
        var probes = harness.Probe.Probes;
        await Eventually.TrueAsync(() =>
        {
            harness.Time.Advance(TimeSpan.FromSeconds(1));
            return harness.Probe.Probes > probes;
        });
        await harness.WaitForStateAsync(2, ReleaseState.Candidate);
        Assert.Equal("one", await harness.CallActiveAsync());
        using var candidate = await harness.CallAsync($"/s/{Id}/r/2/mcp");
        Assert.Equal("two", await candidate.Content.ReadAsStringAsync());

        using var activated = await harness.ActivateAsync(2);

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal("two", await harness.CallActiveAsync());
        Assert.Equal(2, (await harness.StateAsync()).ActiveRelease);
        Assert.Equal(ReleaseState.Active, (await harness.StatusAsync(2)).State);
        Assert.Equal(ReleaseState.Retired, (await harness.StatusAsync(1)).State);
    }

    [Fact]
    public async Task Activation_rechecks_health_and_leaves_the_route_when_the_candidate_went_bad()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await using var one = await FakeUpstream.StartAsync("one");
        harness.Probe.Route(Server(1), one.Address);
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        await harness.BuildAsync(2);
        await harness.StartCandidateAsync(2);

        harness.Probe.SetHealthy(Server(2), false);
        using var refused = await harness.ActivateAsync(2);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("one", await harness.CallActiveAsync());
        Assert.True(harness.Docker.Find(Server(1))!.Running);
    }

    [Fact]
    public async Task Candidate_not_healthy_in_60_seconds_fails_and_is_removed()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);
        harness.Probe.SetHealthy(Server(1), false);

        using var started = await harness.PostStartAsync(1);
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(1))?.Running == true && harness.Probe.Probes > 0);
        harness.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.NotEqual(ReleaseState.Failed, (await harness.StatusAsync(1)).State);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);

        Assert.Contains("60", status.Error, StringComparison.Ordinal);
        Assert.Contains(harness.Docker.ServerLog.Trim(), status.Log, StringComparison.Ordinal);
        Assert.Null(harness.Docker.Find(Server(1)));
        using var route = await harness.CallAsync($"/s/{Id}/r/1/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
    }

    [Fact]
    public async Task Failed_build_keeps_active_release_and_reports_the_log()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await using var one = await FakeUpstream.StartAsync("one");
        harness.Probe.Route(Server(1), one.Address);
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);

        harness.Docker.BuildExitCode = 3;
        harness.Docker.BuildLog = "error: no matching distribution\n";
        using var posted = await harness.PostArchiveAsync(2, Project.Archive(Config("missing-package")));
        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var status = await harness.WaitForStateAsync(2, ReleaseState.Failed);

        Assert.Contains("no matching distribution", status.Log, StringComparison.Ordinal);
        Assert.Contains("3", status.Error, StringComparison.Ordinal);
        Assert.Equal(1, (await harness.StateAsync()).ActiveRelease);
        Assert.Equal("one", await harness.CallActiveAsync());
        Assert.True(harness.Docker.Find(Server(1))!.Running);
        Assert.Null(harness.Docker.Find($"{Prefix}-b2"));
        Assert.DoesNotContain(Code(2), harness.Docker.Volumes);
        Assert.Contains(Code(1), harness.Docker.Volumes);
    }

    [Fact]
    public async Task Failed_build_does_not_leave_a_half_built_environment_for_the_next_one()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        harness.Docker.BuildExitCode = 1;
        using var failing = await harness.PostArchiveAsync(1);
        await harness.WaitForStateAsync(1, ReleaseState.Failed);

        harness.Docker.BuildExitCode = 0;
        await harness.BuildAsync(2);

        Assert.Contains($"{Prefix}-b1", harness.Docker.Created);
        Assert.Contains($"{Prefix}-b2", harness.Docker.Created);
    }

    [Fact]
    public async Task Second_build_of_a_server_is_refused_with_409_while_one_runs()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        harness.Docker.BuildGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var first = await harness.PostArchiveAsync(1);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(ReleaseState.Building, (await harness.StatusAsync(1)).State);
        using var second = await harness.PostArchiveAsync(2);
        using var same = await harness.PostArchiveAsync(1);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Contains("build", await second.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        harness.Docker.BuildGate.SetResult();
        await harness.WaitForStateAsync(1, ReleaseState.Built);
        await harness.BuildAsync(2);
    }

    [Fact]
    public async Task Build_of_an_already_built_release_is_refused()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);

        using var again = await harness.PostArchiveAsync(1);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Build_over_10_minutes_fails_and_removes_its_container()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        harness.Docker.BuildGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var posted = await harness.PostArchiveAsync(1);
        await Eventually.TrueAsync(() => harness.Docker.Find($"{Prefix}-b1")?.Running == true);
        harness.Time.Advance(TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.Equal(ReleaseState.Building, (await harness.StatusAsync(1)).State);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);

        Assert.Contains("10 minutes", status.Error, StringComparison.Ordinal);
        Assert.Null(harness.Docker.Find($"{Prefix}-b1"));
    }

    [Fact]
    public async Task Same_dependencies_reuse_the_environment_volume()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        await harness.BuildAsync(1, Project.Archive(Config("fastmcp==4.1.4", "httpx"), ("mcp/a.py", "a = 1")));
        await harness.BuildAsync(2, Project.Archive(Config("httpx", "fastmcp==4.1.4"), ("mcp/a.py", "a = 2")));

        var first = await harness.StatusAsync(1);
        var second = await harness.StatusAsync(2);
        Assert.Equal(first.EnvironmentHash, second.EnvironmentHash);
        Assert.Equal(first.LockFile, second.LockFile);
        Assert.Contains($"{Prefix}-b1", harness.Docker.Created);
        Assert.DoesNotContain($"{Prefix}-b2", harness.Docker.Created);
        Assert.Contains(Code(1), harness.Docker.Volumes);
        Assert.Contains(Code(2), harness.Docker.Volumes);
        Assert.Null(harness.Docker.Find($"{Prefix}-u1"));
        Assert.Null(harness.Docker.Find($"{Prefix}-u2"));
    }

    [Fact]
    public async Task Different_dependencies_get_their_own_environment()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        await harness.BuildAsync(1, Project.Archive(Config("httpx")));
        await harness.BuildAsync(2, Project.Archive(Config("httpx", "pydantic")));

        Assert.NotEqual((await harness.StatusAsync(1)).EnvironmentHash, (await harness.StatusAsync(2)).EnvironmentHash);
        Assert.Contains($"{Prefix}-b2", harness.Docker.Created);
    }

    [Theory]
    [InlineData(null, "fastmcp.json")]
    [InlineData("not json", "fastmcp.json")]
    [InlineData("""{"source":{}}""", "environment")]
    [InlineData("""{"environment":{"dependencies":[1]}}""", "dependencies")]
    public async Task Invalid_or_missing_fastmcp_json_fails_with_a_clear_message(string? config, string expected)
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var posted = await harness.PostArchiveAsync(1, Project.Archive(config, ("mcp/a.py", "a = 1")));

        Assert.Equal(HttpStatusCode.Accepted, posted.StatusCode);
        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);
        Assert.Contains(expected, status.Error, StringComparison.Ordinal);
        Assert.Empty(harness.Docker.Created);
    }

    [Fact]
    public async Task Body_that_is_not_an_archive_fails_with_a_clear_message()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();

        using var posted = await harness.PostArchiveAsync(1, [1, 2, 3, 4]);

        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);
        Assert.Contains("archive", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_engine_failure_in_a_background_build_is_reported_and_the_process_survives()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        harness.Docker.BeforeCreate = _ => new IOException("disk full");

        using var posted = await harness.PostArchiveAsync(1);
        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);

        Assert.Contains("disk full", status.Error, StringComparison.Ordinal);
        harness.Docker.BeforeCreate = null;
        await harness.BuildAsync(2);
    }

    [Fact]
    public async Task Archive_reaches_the_code_volume_through_a_container_that_never_runs()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        var archive = Project.Archive();
        harness.Docker.BuildGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var posted = await harness.PostArchiveAsync(1, archive);
        await Eventually.TrueAsync(() => harness.Docker.Find($"{Prefix}-b1") is not null);

        Assert.Contains($"{Prefix}-u1", harness.Docker.Created);
        Assert.Null(harness.Docker.Find($"{Prefix}-u1"));
        var extraction = Assert.Single(harness.Docker.Extractions);
        Assert.Equal($"{Prefix}-u1", extraction.Container);
        Assert.Equal("/code", extraction.Path);
        Assert.Equal(archive, extraction.Archive);
        Assert.False(extraction.ContainerRunning);
        harness.Docker.BuildGate.SetResult();
    }

    [Fact]
    public async Task Old_release_is_stopped_after_30_seconds_and_only_five_newest_keep_volumes()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1, Project.Archive(Config("old-package")));

        await harness.PublishAsync(2);
        Assert.True(harness.Docker.Find(Server(1))!.Running);
        harness.Time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        Assert.True(harness.Docker.Find(Server(1))!.Running);
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(1))?.Running == false);
        Assert.Contains(Code(1), harness.Docker.Volumes);

        var oldEnvironment = $"{Prefix}-env-{(await harness.StatusAsync(1)).EnvironmentHash![..12]}";
        var currentEnvironment = $"{Prefix}-env-{(await harness.StatusAsync(2)).EnvironmentHash![..12]}";
        Assert.Contains(oldEnvironment, harness.Docker.Volumes);

        for (var release = 3; release <= 6; release++)
        {
            await harness.PublishAsync(release);
            harness.Time.Advance(TimeSpan.FromSeconds(30));
            var previous = Server(release - 1);
            await Eventually.TrueAsync(() => harness.Docker.Find(previous)?.Running == false);
        }

        await Eventually.TrueAsync(() => !harness.Docker.Volumes.Contains(Code(1)));
        await Eventually.TrueAsync(() => !harness.Docker.Volumes.Contains(oldEnvironment));
        Assert.Equal(
            Enumerable.Range(2, 5).Select(Code).Order().ToList(),
            harness.Docker.Volumes.Where(volume => volume.StartsWith($"{Prefix}-code-", StringComparison.Ordinal)).Order().ToList());
        Assert.Contains(currentEnvironment, harness.Docker.Volumes);
        Assert.Contains($"{Prefix}-data", harness.Docker.Volumes);
        Assert.Contains($"{Prefix}-uvcache", harness.Docker.Volumes);
        Assert.Null(harness.Docker.Find(Server(1)));
        using var forgotten = await harness.Control.GetAsync($"/v1/servers/{Id}/releases/1");
        Assert.Equal(HttpStatusCode.NotFound, forgotten.StatusCode);
    }

    [Fact]
    public async Task A_new_candidate_replaces_the_previous_one()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);
        await harness.BuildAsync(2);
        await harness.StartCandidateAsync(1);

        await harness.StartCandidateAsync(2);

        Assert.Null(harness.Docker.Find(Server(1)));
        Assert.Equal(ReleaseState.Built, (await harness.StatusAsync(1)).State);
        using var route = await harness.CallAsync($"/s/{Id}/r/1/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
    }

    [Fact]
    public async Task Build_over_an_active_release_is_refused()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);

        using var again = await harness.PostArchiveAsync(1);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Failed_start_can_be_retried_without_a_new_build()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);
        harness.Probe.SetHealthy(Server(1), false);
        using var failing = await harness.PostStartAsync(1);
        await Eventually.TrueAsync(() => harness.Probe.Probes > 0);
        harness.Time.Advance(TimeSpan.FromSeconds(60));
        await harness.WaitForStateAsync(1, ReleaseState.Failed);

        harness.Probe.SetHealthy(Server(1), true);
        await harness.StartCandidateAsync(1);

        using var rebuild = await harness.PostArchiveAsync(1);
        Assert.Equal(HttpStatusCode.Conflict, rebuild.StatusCode);
    }

    [Fact]
    public async Task Rolled_back_old_release_keeps_its_volumes_outside_the_newest_five()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        await harness.PublishAsync(2);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(1))?.Running == false);

        await harness.StartCandidateAsync(1);
        using var rollback = await harness.ActivateAsync(1);
        Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        Assert.Equal(1, (await harness.StateAsync()).ActiveRelease);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(2))?.Running == false);

        for (var release = 3; release <= 8; release++)
        {
            await harness.BuildAsync(release);
        }

        await harness.StartCandidateAsync(8);
        using var activated = await harness.ActivateAsync(8);
        Assert.Equal(8, (await harness.StateAsync()).ActiveRelease);
        await harness.StartCandidateAsync(1);
        using var back = await harness.ActivateAsync(1);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => !harness.Docker.Volumes.Contains(Code(2)));

        Assert.Contains(Code(1), harness.Docker.Volumes);
        Assert.Equal(1, (await harness.StateAsync()).ActiveRelease);
    }

    [Fact]
    public async Task Candidate_of_the_active_release_runs_beside_it_with_the_new_secret()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await using var one = await FakeUpstream.StartAsync("one");
        await harness.PutSpecAsync();
        harness.Probe.Route(Server(1), one.Address);
        await harness.PublishAsync(1);

        await harness.PutSpecAsync(RunnerHarness.Spec(secret: "rotated"));
        await harness.StartCandidateAsync(1, new Dictionary<string, string> { ["API_KEY"] = "fresh" });

        var replacement = harness.Docker.Containers.Single(container =>
            container.Release == 1 && container.Role is null && container.Name != Server(1));
        Assert.Contains("API_KEY=fresh", replacement.Parameters.Env);
        Assert.True(harness.Docker.Find(Server(1))!.Running);
        Assert.Equal("one", await harness.CallActiveAsync());

        using var activated = await harness.ActivateAsync(1);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(ReleaseState.Active, (await harness.StatusAsync(1)).State);
        using var oldSecret = await harness.CallAsync($"/s/{Id}/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, oldSecret.StatusCode);

        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => harness.Docker.Find(Server(1)) is null);
        Assert.True(replacement.Running);
        Assert.Equal(1, (await harness.StateAsync()).ActiveRelease);
    }

    [Fact]
    public async Task Internet_only_server_attaches_runner_and_egress_and_full_network_only_runner()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);

        Assert.Contains(($"{Prefix}-internet", "runner-self"), harness.Docker.Attachments);
        Assert.Contains(($"{Prefix}-internet", "egress"), harness.Docker.Attachments);

        await harness.PutSpecAsync(RunnerHarness.Spec(NetworkAccess.FullNetwork));
        await harness.StartCandidateAsync(1);

        Assert.Contains(($"{Prefix}-full", "runner-self"), harness.Docker.Attachments);
        Assert.DoesNotContain(($"{Prefix}-full", "egress"), harness.Docker.Attachments);
    }

    [Fact]
    public async Task Access_change_starts_a_candidate_on_a_second_network_and_removes_the_old_one_after_retiring()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        Assert.Equal([$"{Prefix}-internet"], harness.Docker.Networks);

        await harness.PutSpecAsync(RunnerHarness.Spec(NetworkAccess.FullNetwork));
        Assert.Equal([$"{Prefix}-internet"], harness.Docker.Networks);
        await harness.StartCandidateAsync(1);

        Assert.Equal(2, harness.Docker.Networks.Count);
        Assert.True(harness.Docker.Find(Server(1))!.Running);
        var candidate = harness.Docker.Containers.Single(container => container.Release == 1 && container.Role is null && container.Name != Server(1));
        Assert.Equal($"{Prefix}-full", candidate.Parameters.HostConfig.NetworkMode);

        using var activated = await harness.ActivateAsync(1);
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Contains($"{Prefix}-internet", harness.Docker.Networks);

        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => harness.Docker.RemovedNetworks.Contains($"{Prefix}-internet"));

        Assert.Equal([$"{Prefix}-full"], harness.Docker.Networks);
        Assert.DoesNotContain(harness.Docker.Attachments, attachment => attachment.Network == $"{Prefix}-internet");
        Assert.True(candidate.Running);
    }

    [Fact]
    public async Task Each_network_gets_a_free_subnet_from_the_pool()
    {
        await using var harness = await RunnerHarness.StartAsync();
        harness.Docker.Subnets = ["10.213.0.0/28"];
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);
        await harness.PutSpecAsync(RunnerHarness.Spec(NetworkAccess.FullNetwork));
        await harness.StartCandidateAsync(1);

        var taken = await harness.Docker.ListSubnetsAsync(CancellationToken.None);

        Assert.Contains("10.213.0.16/28", taken);
        Assert.Contains("10.213.0.32/28", taken);
    }

    [Fact]
    public async Task Reconciler_restores_routes_from_container_labels()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));
        await using var one = await FakeUpstream.StartAsync("one");
        await using var two = await FakeUpstream.StartAsync("two");
        await using var three = await FakeUpstream.StartAsync("three");
        probe.Route(Server(1), one.Address);
        probe.Route(Server(2), two.Address);
        probe.Route(Server(3), three.Address);

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
            await first.PublishAsync(2);
            first.Time.Advance(TimeSpan.FromSeconds(30));
            await Eventually.TrueAsync(() => docker.Find(Server(1))?.Running == false);
            await first.BuildAsync(3);
            await first.StartCandidateAsync(3);
        }

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal("two", await restarted.CallActiveAsync());
        using var candidate = await restarted.CallAsync($"/s/{Id}/r/3/mcp");
        Assert.Equal("three", await candidate.Content.ReadAsStringAsync());
        using var wrong = await restarted.CallAsync($"/s/{Id}/mcp", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var state = await restarted.StateAsync();
        Assert.Equal(2, state.ActiveRelease);
        Assert.Equal(ReleaseState.Retired, (await restarted.StatusAsync(1)).State);
        Assert.Equal(ReleaseState.Active, (await restarted.StatusAsync(2)).State);
        Assert.Equal(ReleaseState.Candidate, (await restarted.StatusAsync(3)).State);
        Assert.NotNull((await restarted.StatusAsync(3)).EnvironmentHash);
    }

    [Fact]
    public async Task Rolled_back_active_release_survives_a_restart_as_active()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
            await first.PublishAsync(2);
            first.Time.Advance(TimeSpan.FromSeconds(30));
            await Eventually.TrueAsync(() => docker.Find(Server(1))?.Running == false);
            await first.StartCandidateAsync(1);
            using var rollback = await first.ActivateAsync(1);
            Assert.Equal(HttpStatusCode.OK, rollback.StatusCode);
        }

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal(1, (await restarted.StateAsync()).ActiveRelease);
    }

    [Fact]
    public async Task Release_that_was_retiring_during_a_restart_is_still_stopped()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
            await first.PublishAsync(2);
        }

        Assert.True(docker.Find(Server(1))!.Running);
        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        restarted.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => docker.Find(Server(1))?.Running == false);
        Assert.Equal(ReleaseState.Retired, (await restarted.StatusAsync(1)).State);
        using var candidate = await restarted.CallAsync($"/s/{Id}/r/1/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, candidate.StatusCode);
    }

    [Fact]
    public async Task Two_releases_retiring_during_a_restart_are_both_stopped()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
            await first.PublishAsync(2);
            await first.PublishAsync(3);
        }

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal(ReleaseState.Retired, (await restarted.StatusAsync(1)).State);
        Assert.Equal(ReleaseState.Retired, (await restarted.StatusAsync(2)).State);
        restarted.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => docker.Find(Server(1))?.Running == false && docker.Find(Server(2))?.Running == false);
    }

    [Fact]
    public async Task Reconciler_reattaches_system_containers_to_server_networks()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
        }

        await docker.DisconnectAsync(Id, $"{Prefix}-internet", "runner-self", CancellationToken.None);
        await docker.DisconnectAsync(Id, $"{Prefix}-internet", "egress", CancellationToken.None);

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Contains(($"{Prefix}-internet", "runner-self"), docker.Attachments);
        Assert.Contains(($"{Prefix}-internet", "egress"), docker.Attachments);
    }

    [Fact]
    public async Task Reconciler_marks_interrupted_build_failed_and_removes_its_container()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));
        docker.BuildGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            using var posted = await first.PostArchiveAsync(1);
            await Eventually.TrueAsync(() => docker.Find($"{Prefix}-b1")?.Running == true);
        }

        Assert.NotNull(docker.Find($"{Prefix}-b1"));
        docker.BuildGate = null;

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Null(docker.Find($"{Prefix}-b1"));
        var status = await restarted.StatusAsync(1);
        Assert.Equal(ReleaseState.Failed, status.State);
        Assert.Contains("interrupted", status.Error, StringComparison.OrdinalIgnoreCase);

        using var withoutSpec = await restarted.PostArchiveAsync(2);
        Assert.Equal(HttpStatusCode.Conflict, withoutSpec.StatusCode);

        await restarted.PutSpecAsync();
        await restarted.BuildAsync(2);
        Assert.Contains($"{Prefix}-b2", docker.Created);
        await restarted.BuildAsync(1);
    }

    [Fact]
    public async Task Release_unknown_after_a_restart_returns_404_and_start_without_spec_is_a_conflict()
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));

        await using (var first = await RunnerHarness.StartAsync(docker, probe, time))
        {
            await first.PutSpecAsync();
            await first.PublishAsync(1);
            await first.BuildAsync(2);
        }

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        using var unknown = await restarted.Control.GetAsync($"/v1/servers/{Id}/releases/2");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var withoutSpec = await restarted.PostStartAsync(1);
        Assert.Equal(HttpStatusCode.Conflict, withoutSpec.StatusCode);
        Assert.Contains("PUT", await withoutSpec.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await restarted.PutSpecAsync();
        using var unknownStart = await restarted.PostStartAsync(2);
        Assert.Equal(HttpStatusCode.NotFound, unknownStart.StatusCode);
    }

    private static bool IsMarker(string name) => name.Contains("-active-", StringComparison.Ordinal);

    private static async Task<(FakeDockerEngine Docker, FakeServerProbe Probe, FakeTimeProvider Time)> FirstRunAsync(Func<RunnerHarness, Task> run)
    {
        var docker = new FakeDockerEngine();
        var probe = new FakeServerProbe();
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));
        await using var first = await RunnerHarness.StartAsync(docker, probe, time);
        await first.PutSpecAsync();
        await run(first);
        return (docker, probe, time);
    }

    [Fact]
    public async Task Lost_marker_keeps_the_running_release_until_a_new_one_is_activated()
    {
        var (docker, probe, time) = await FirstRunAsync(first => first.PublishAsync(1));
        docker.LoseActivationMarkers();

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.True(docker.Find(Server(1))!.Running);
        Assert.Null((await restarted.StateAsync()).ActiveRelease);

        await restarted.PutSpecAsync();
        await restarted.BuildAsync(2);
        await restarted.StartCandidateAsync(2);

        Assert.True(docker.Find(Server(1))!.Running);
        await using var two = await FakeUpstream.StartAsync("two");

        using var activated = await restarted.ActivateAsync(2);

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(2, (await restarted.StateAsync()).ActiveRelease);
        Assert.True(docker.Find(Server(1))!.Running);
        restarted.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => docker.Find(Server(1))?.Running == false);
        Assert.Equal(ReleaseState.Retired, (await restarted.StatusAsync(1)).State);
    }

    [Fact]
    public async Task Lost_marker_lets_the_running_release_be_activated_again()
    {
        var (docker, probe, time) = await FirstRunAsync(first => first.PublishAsync(1));
        docker.LoseActivationMarkers();

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);
        using var activated = await restarted.ActivateAsync(1);

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(1, (await restarted.StateAsync()).ActiveRelease);
        Assert.Single(docker.ActivationMarkers);
        restarted.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.True(docker.Find(Server(1))!.Running);
    }

    [Fact]
    public async Task Same_release_candidate_does_not_replace_a_running_release_without_a_marker()
    {
        var (docker, probe, time) = await FirstRunAsync(first => first.PublishAsync(1));
        docker.LoseActivationMarkers();

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);
        await restarted.PutSpecAsync();
        await restarted.StartCandidateAsync(1);

        Assert.True(docker.Find(Server(1))!.Running);
        Assert.Equal(2, docker.Containers.Count(container => container.Release == 1 && container.Role is null));
    }

    [Fact]
    public async Task Marker_naming_a_missing_container_is_removed_and_the_running_release_stays()
    {
        var (docker, probe, time) = await FirstRunAsync(async first =>
        {
            await first.PublishAsync(1);
            await first.BuildAsync(2);
            await first.StartCandidateAsync(2);
        });
        await docker.RemoveContainerAsync(Id, Server(1), CancellationToken.None);

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Empty(docker.ActivationMarkers);
        Assert.Null((await restarted.StateAsync()).ActiveRelease);
        Assert.True(docker.Find(Server(2))!.Running);
    }

    [Fact]
    public async Task Marker_that_cannot_be_written_leaves_the_old_release_active_also_after_a_restart()
    {
        var (docker, probe, time) = await FirstRunAsync(async first =>
        {
            await first.PublishAsync(1);
            await first.BuildAsync(2);
            await first.StartCandidateAsync(2);
            first.Docker.BeforeCreate = name => IsMarker(name) ? new IOException("disk full") : null;

            using var refused = await first.ActivateAsync(2);

            Assert.False(refused.IsSuccessStatusCode);
            Assert.Equal(1, (await first.StateAsync()).ActiveRelease);
        });
        docker.BeforeCreate = null;

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal(1, (await restarted.StateAsync()).ActiveRelease);
    }

    [Fact]
    public async Task Marker_written_with_a_lost_reply_is_withdrawn_so_a_restart_stays_on_the_old_release()
    {
        var (docker, probe, time) = await FirstRunAsync(async first =>
        {
            await first.PublishAsync(1);
            await first.BuildAsync(2);
            await first.StartCandidateAsync(2);
            first.Docker.AfterCreate = name => IsMarker(name) ? new IOException("connection lost") : null;

            using var refused = await first.ActivateAsync(2);

            Assert.False(refused.IsSuccessStatusCode);
            Assert.Equal(1, (await first.StateAsync()).ActiveRelease);
        });
        docker.AfterCreate = null;

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal(1, (await restarted.StateAsync()).ActiveRelease);
    }

    [Fact]
    public async Task Old_marker_that_cannot_be_removed_does_not_fail_the_activation()
    {
        var (docker, probe, time) = await FirstRunAsync(async first =>
        {
            await first.PublishAsync(1);
            await first.BuildAsync(2);
            await first.StartCandidateAsync(2);
            first.Docker.BeforeRemove = name => IsMarker(name) ? new IOException("docker error") : null;

            using var activated = await first.ActivateAsync(2);

            Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
            Assert.Equal(2, (await first.StateAsync()).ActiveRelease);
            Assert.Equal(2, first.Docker.ActivationMarkers.Count);
        });
        docker.BeforeRemove = null;

        await using var restarted = await RunnerHarness.StartAsync(docker, probe, time);

        Assert.Equal(2, (await restarted.StateAsync()).ActiveRelease);
        Assert.Single(docker.ActivationMarkers);
    }

    [Fact]
    public async Task Client_leaving_during_activation_cannot_split_memory_from_the_marker()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        await harness.BuildAsync(2);
        await harness.StartCandidateAsync(2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Docker.AfterCreateGate = name => IsMarker(name) ? gate : null;

        using var leaving = new CancellationTokenSource();
        var request = harness.Control.PostAsync($"/v1/servers/{Id}/releases/2/activate", null, leaving.Token);
        await Eventually.TrueAsync(() => harness.Docker.ActivationMarkers.Count == 2);
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Task.Delay(100);
        gate.SetResult();

        await Eventually.TrueAsync(async () => (await harness.StateAsync()).ActiveRelease == 2);
        await Eventually.TrueAsync(() => harness.Docker.ActivationMarkers.Count == 1);
    }

    [Fact]
    public async Task Delete_waits_for_activations_in_flight_and_leaves_no_route_or_marker()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await using var two = await FakeUpstream.StartAsync("two");
        harness.Probe.Route(Server(2), two.Address);
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);
        await harness.BuildAsync(2);
        await harness.StartCandidateAsync(2);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Probe.Gate = gate;
        var probes = harness.Probe.Probes;
        var inFlight = harness.ActivateAsync(2);
        await Eventually.TrueAsync(() => harness.Probe.Probes > probes);
        var queued = harness.Control.PostAsync($"/v1/servers/{Id}/releases/2/activate", null);
        await Task.Delay(50);
        var deleted = harness.Control.DeleteAsync($"/v1/servers/{Id}");
        await Task.Delay(100);
        Assert.False(deleted.IsCompleted);

        gate.SetResult();
        harness.Probe.Gate = null;
        using var deletedResponse = await deleted;
        using var inFlightResponse = await inFlight;
        using var queuedResponse = await queued;

        Assert.Equal(HttpStatusCode.NoContent, deletedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, queuedResponse.StatusCode);
        using var route = await harness.CallAsync($"/s/{Id}/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, route.StatusCode);
        Assert.Empty(harness.Docker.ActivationMarkers);
        Assert.Empty(harness.Docker.Containers);
        Assert.Empty(harness.Docker.Volumes);
    }

    [Fact]
    public async Task Oversized_lock_file_fails_the_build_and_drops_the_environment()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        var normal = harness.Docker.LockFile;
        harness.Docker.LockFile = new string('x', 2 * 1024 * 1024);

        using var posted = await harness.PostArchiveAsync(1);
        var status = await harness.WaitForStateAsync(1, ReleaseState.Failed);

        Assert.Contains("requirements.lock", status.Error, StringComparison.Ordinal);
        Assert.Contains("limit", status.Error, StringComparison.Ordinal);
        Assert.Null(status.LockFile);
        Assert.DoesNotContain(harness.Docker.Volumes, volume => volume.Contains("-env-", StringComparison.Ordinal));

        harness.Docker.LockFile = normal;
        await harness.BuildAsync(2);
        Assert.Contains($"{Prefix}-b2", harness.Docker.Created);
    }

    [Fact]
    public async Task Oversized_lock_in_a_reused_environment_fails_and_drops_that_environment()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.BuildAsync(1);
        var normal = harness.Docker.LockFile;
        harness.Docker.LockFile = new string('x', 2 * 1024 * 1024);

        using var posted = await harness.PostArchiveAsync(2);
        await harness.WaitForStateAsync(2, ReleaseState.Failed);

        harness.Docker.LockFile = normal;
        await harness.BuildAsync(3);
        Assert.Contains($"{Prefix}-b3", harness.Docker.Created);
    }

    [Fact]
    public async Task Build_log_flood_keeps_only_the_tail_in_the_status()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        harness.Docker.BuildLog = new string('x', 2 * 1024 * 1024) + "last line\n";

        await harness.BuildAsync(1);

        var log = (await harness.StatusAsync(1)).Log;
        Assert.True(log.Length <= ReleaseRecord.MaxLogCharacters);
        Assert.EndsWith("last line\n", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_secret_label_is_a_hash_and_the_raw_secret_is_in_no_container()
    {
        await using var harness = await RunnerHarness.StartAsync();
        await harness.PutSpecAsync();
        await harness.PublishAsync(1);

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RunnerHarness.Secret)));
        Assert.Equal(expected, harness.Docker.Find(Server(1))!.Labels[BuilderLabels.RouteSecretHash]);
        foreach (var container in harness.Docker.Containers)
        {
            Assert.DoesNotContain(RunnerHarness.Secret, container.Labels.Values);
            Assert.DoesNotContain(container.Parameters.Env ?? [], entry => entry.Contains(RunnerHarness.Secret, StringComparison.Ordinal));
        }
    }
}
