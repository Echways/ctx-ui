using CtxUi.Builder.Contracts;
using CtxUi.Builder.Data;
using CtxUi.Builder.Publishing;
using CtxUi.Builder.Servers;
using Enfilade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class ConnectionServiceTests
{
    [Fact]
    public async Task Connect_creates_inbound_with_route_secret_and_links_clients()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var first = harness.Data.AddClient("first");
        harness.Data.AddClient("second");

        var inbound = await harness.ConnectAsync(server, "weather-mcp", "Weather", true, first.Id);

        var draft = Assert.Single(harness.Data.Drafts);
        Assert.Equal("weather-mcp", draft.Slug);
        Assert.Equal("Weather", draft.Name);
        Assert.True(draft.ForwardIdentity);
        Assert.Equal($"http://runner:8090/s/{server}/mcp", draft.Url);
        Assert.Equal(await harness.RouteSecretAsync(server), Assert.Single(draft.UpstreamHeaders!, header => header.Key == RouteSecret.Header).Value);
        Assert.Equal([first.Id], harness.Data.Links["weather-mcp"]);
        Assert.Equal(inbound.Id.ToString(), (await harness.ServerAsync(server)).InboundId);
        Assert.Equal(inbound, await harness.FindInboundAsync(server));
    }

    [Fact]
    public async Task Connect_without_clients_leaves_every_client_link_untouched()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var client = harness.Data.AddClient("only");
        harness.Data.AddInbound("existing");
        harness.Data.Links["existing"] = [client.Id];

        await harness.ConnectAsync(server, "weather-mcp");

        Assert.Equal([client.Id], harness.Data.Links["existing"]);
        Assert.Empty(harness.Data.Links["weather-mcp"]);
    }

    [Fact]
    public async Task Taken_slug_shows_the_reason_and_leaves_the_server_unconnected()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var client = harness.Data.AddClient("first");
        harness.Data.AddInbound("weather-mcp");

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "weather-mcp", "Weather", false, client.Id));

        Assert.Equal(BuilderText.InboundSlugTaken, failure.Message);
        Assert.Null((await harness.ServerAsync(server)).InboundId);
        Assert.Empty(harness.Data.Drafts);
        Assert.Empty(harness.Data.Links["weather-mcp"]);
        Assert.Null(await harness.FindInboundAsync(server));
    }

    [Fact]
    public async Task A_slug_taken_in_the_meantime_is_reported_with_the_reason_of_the_panel()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Data.AddFailure = new PanelException("Slug is invalid.");

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "Bad Slug"));

        Assert.Equal(string.Format(BuilderText.InboundRejected, "Slug is invalid."), failure.Message);
        Assert.Null((await harness.ServerAsync(server)).InboundId);
    }

    [Fact]
    public async Task An_unknown_client_removes_the_inbound_again_and_leaves_the_server_unconnected()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();

        await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "weather-mcp", "Weather", false, Guid.NewGuid()));

        Assert.Empty(harness.Data.Inbounds);
        Assert.Null((await harness.ServerAsync(server)).InboundId);
    }

    [Fact]
    public async Task A_connected_server_cannot_be_connected_twice()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.ConnectAsync(server, "weather-mcp");

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "another"));

        Assert.Equal(BuilderText.ConnectionAlreadyConnected, failure.Message);
        Assert.Single(harness.Data.Inbounds);
    }

    [Fact]
    public async Task Incomplete_runner_settings_stop_the_connection_with_the_missing_names()
    {
        await using var harness = await PublishHarness.CreateAsync(configured: false);
        var server = await harness.CreateServerAsync();

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "weather-mcp"));

        Assert.Equal(string.Format(BuilderText.RunnerNotConfigured, "RunnerUrl, RunnerIngressUrl, RunnerToken"), failure.Message);
        Assert.Empty(harness.Data.Inbounds);
    }

    [Fact]
    public async Task Inbound_removed_elsewhere_shows_as_not_connected()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.ConnectAsync(server, "weather-mcp");
        harness.Data.Inbounds.Clear();

        Assert.Null(await harness.FindInboundAsync(server));

        var again = await harness.ConnectAsync(server, "weather-mcp");
        Assert.Equal(again, await harness.FindInboundAsync(server));
    }

    [Fact]
    public async Task Deleting_a_connected_server_removes_inbound_and_runner_objects()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        var release = await harness.ReleaseAsync(server, 1);
        await harness.ConnectAsync(server, "weather-mcp");
        harness.Data.OnRemove = _ => Assert.True(harness.Runner.ServerDeleted);

        await harness.DeleteServerAsync(server);

        Assert.True(harness.Runner.ServerDeleted);
        Assert.Empty(harness.Data.Inbounds);
        Assert.False(await harness.ServerExistsAsync(server));
        Assert.Empty(await harness.ReleasesAsync(server));
        Assert.False(File.Exists(harness.Archives.PathOf(release.Id)));
    }

    [Fact]
    public async Task Deleting_a_server_without_an_inbound_or_archives_succeeds()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();

        await harness.DeleteServerAsync(server);

        Assert.False(await harness.ServerExistsAsync(server));
    }

    [Fact]
    public async Task Runner_failure_during_delete_keeps_the_server_and_the_inbound()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        var release = await harness.ReleaseAsync(server, 1);
        await harness.ConnectAsync(server, "weather-mcp");
        harness.Runner.FailDeletes = true;

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.DeleteServerAsync(server));

        Assert.Equal(string.Format(BuilderText.RunnerDeleteFailed, "The runner could not remove the server."), failure.Message);
        Assert.Single(harness.Data.Inbounds);
        Assert.True(await harness.ServerExistsAsync(server));
        Assert.True(File.Exists(harness.Archives.PathOf(release.Id)));
    }

    [Fact]
    public async Task A_server_that_is_being_published_cannot_be_deleted()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        harness.Runner.HoldBuilds = true;
        await harness.Queue.PublishAsync(server, PublishMode.Activate, default);

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.DeleteServerAsync(server));

        Assert.Equal(BuilderText.PublishBusy, failure.Message);
        Assert.True(await harness.ServerExistsAsync(server));
        Assert.False(harness.Runner.ServerDeleted);
    }

    [Fact]
    public async Task The_first_activation_of_a_server_without_an_inbound_is_announced_once()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var announced = new List<Guid>();
        harness.Queue.FirstActivated += announced.Add;
        await harness.StartAsync();

        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);
        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);

        Assert.Equal([server], announced);
    }

    [Fact]
    public async Task A_build_only_publication_and_a_connected_server_are_not_announced()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var buildOnly = await harness.CreateServerAsync("first");
        var connected = await harness.CreateServerAsync("second");
        await harness.ConnectAsync(connected, "second-mcp");
        var announced = new List<Guid>();
        harness.Queue.FirstActivated += announced.Add;
        await harness.StartAsync();

        await harness.PublishAndWaitAsync(buildOnly, PublishMode.BuildOnly);
        await harness.WaitIdleAsync(buildOnly);
        await harness.PublishAndWaitAsync(connected);
        await harness.WaitIdleAsync(connected);

        Assert.Empty(announced);
    }

    [Fact]
    public async Task A_connect_that_overlaps_another_one_is_refused_and_leaves_one_inbound()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        Exception? refused = null;
        harness.Data.DuringAdd = async () =>
        {
            try
            {
                await harness.ConnectAsync(server, "second-mcp");
            }
            catch (PanelException failure)
            {
                refused = failure;
            }
        };

        var inbound = await harness.ConnectAsync(server, "first-mcp");

        Assert.Equal(BuilderText.ConnectionBusy, refused?.Message);
        Assert.Equal([inbound.Id], harness.Data.Inbounds.Select(known => known.Id));
        Assert.Equal(inbound.Id.ToString(), (await harness.ServerAsync(server)).InboundId);
    }

    [Fact]
    public async Task A_connection_written_by_someone_else_in_the_meantime_removes_the_new_inbound()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var client = harness.Data.AddClient("first");
        var other = Guid.NewGuid().ToString();
        harness.Data.DuringAdd = () => harness.SetInboundIdAsync(server, other);

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "weather-mcp", "Weather", false, client.Id));

        Assert.Equal(BuilderText.ConnectionAlreadyConnected, failure.Message);
        Assert.Empty(harness.Data.Inbounds);
        Assert.Equal(other, (await harness.ServerAsync(server)).InboundId);
    }

    [Fact]
    public async Task A_server_deleted_in_the_meantime_removes_the_new_inbound()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Data.DuringAdd = () => harness.RemoveServerRowAsync(server);

        var failure = await Assert.ThrowsAsync<PanelException>(() => harness.ConnectAsync(server, "weather-mcp"));

        Assert.Equal(BuilderText.ServerNotFound, failure.Message);
        Assert.Empty(harness.Data.Inbounds);
    }

    [Fact]
    public async Task A_delete_that_overlaps_a_connect_is_refused()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        Exception? refused = null;
        harness.Data.DuringAdd = async () =>
        {
            try
            {
                await harness.DeleteServerAsync(server);
            }
            catch (PanelException failure)
            {
                refused = failure;
            }
        };

        await harness.ConnectAsync(server, "weather-mcp");

        Assert.Equal(BuilderText.ConnectionBusy, refused?.Message);
        Assert.True(await harness.ServerExistsAsync(server));
        Assert.False(harness.Runner.ServerDeleted);
    }

    [Fact]
    public async Task A_connect_during_a_delete_is_refused()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.Queue.ReserveForDeletion(server);

        var refused = await Record.ExceptionAsync(() => harness.ConnectAsync(server, "weather-mcp"));

        Assert.Equal(BuilderText.ServerDeleting, refused?.Message);
        Assert.Empty(harness.Data.Drafts);
    }

    [Fact]
    public async Task A_server_reserved_for_deletion_refuses_publications_and_settings_changes_until_released()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);
        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);
        harness.Queue.ReserveForDeletion(server);

        var publish = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.PublishAsync(server, PublishMode.Activate, default));
        var rollback = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.RollbackAsync(server, 1, default));
        var settings = await Assert.ThrowsAsync<PanelException>(() => harness.Queue.ApplySettingsAsync(server, default));

        Assert.All([publish, rollback, settings], failure => Assert.Equal(BuilderText.ServerDeleting, failure.Message));
        Assert.False(harness.Queue.IsBusy(server));

        harness.Queue.ReleaseDeletion(server);
        await harness.PublishAndWaitAsync(server);
    }

    [Fact]
    public async Task A_publication_in_progress_blocks_the_reservation()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        harness.Runner.HoldBuilds = true;
        await harness.Queue.PublishAsync(server, PublishMode.Activate, default);

        var failure = Assert.Throws<PanelException>(() => harness.Queue.ReserveForDeletion(server));

        Assert.Equal(BuilderText.PublishBusy, failure.Message);
    }

    [Fact]
    public async Task Repair_does_not_run_for_a_server_being_deleted()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        harness.RepairInterval = TimeSpan.FromMilliseconds(40);
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        await harness.WaitIdleAsync(server);
        harness.Queue.ReserveForDeletion(server);
        harness.Runner.Restart(loseMarker: true);

        await Task.Delay(300);

        Assert.Null(harness.Runner.Active);
        harness.Queue.ReleaseDeletion(server);
        await harness.WaitForAsync(() => Task.FromResult(harness.Runner.Active == 1));
    }

    [Fact]
    public async Task A_failed_delete_releases_the_reservation()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        harness.Runner.FailDeletes = true;

        await Assert.ThrowsAsync<PanelException>(() => harness.DeleteServerAsync(server));

        harness.Runner.FailDeletes = false;
        await harness.PublishAndWaitAsync(server);
    }

    [Fact]
    public async Task A_missing_archive_directory_does_not_fail_the_delete()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        Directory.Delete(Path.GetDirectoryName(harness.Archives.PathOf(Guid.Empty))!, true);

        await harness.DeleteServerAsync(server);

        Assert.False(await harness.ServerExistsAsync(server));
    }

    [Fact]
    public async Task An_archive_that_cannot_be_removed_does_not_fail_the_delete_after_the_commit()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        await harness.StartAsync();
        await harness.PublishAndWaitAsync(server);
        var path = harness.Archives.PathOf((await harness.ReleaseAsync(server, 1)).Id);
        File.Delete(path);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "block"), "x");

        await harness.DeleteServerAsync(server);

        Assert.False(await harness.ServerExistsAsync(server));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task The_set_of_connected_inbounds_follows_connect_delete_and_removal_elsewhere()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var first = await harness.CreateServerAsync("first");
        var second = await harness.CreateServerAsync("second");
        var inbound = await harness.ConnectAsync(first, "first-mcp");
        var other = await harness.ConnectAsync(second, "second-mcp");

        Assert.True(harness.Connected.Contains(inbound.Id));
        Assert.True(harness.Connected.Contains(other.Id));

        harness.Data.Inbounds.RemoveAll(known => known.Id == other.Id);
        await harness.FindInboundAsync(second);
        Assert.False(harness.Connected.Contains(other.Id));

        await harness.DeleteServerAsync(first);
        Assert.False(harness.Connected.Contains(inbound.Id));
    }

    [Fact]
    public async Task The_set_of_connected_inbounds_is_loaded_from_the_database_at_start()
    {
        await using var harness = await PublishHarness.CreateAsync();
        var server = await harness.CreateServerAsync();
        var inbound = await harness.ConnectAsync(server, "weather-mcp");
        var fresh = new ConnectedInbounds();

        await new ConnectedInboundsLoader(fresh, harness.Services.GetRequiredService<IDbContextFactory<BuilderDatabase>>()).InitializeAsync(default);

        Assert.True(fresh.Contains(inbound.Id));
    }
}
