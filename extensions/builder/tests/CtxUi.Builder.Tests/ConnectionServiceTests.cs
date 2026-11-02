using CtxUi.Builder.Contracts;
using CtxUi.Builder.Publishing;
using Enfilade;

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
        harness.Data.AddFailure = new Core.DomainException("Slug is invalid.");

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
}
