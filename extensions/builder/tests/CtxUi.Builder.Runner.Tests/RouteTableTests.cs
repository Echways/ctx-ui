using CtxUi.Builder.Contracts;
using Microsoft.Extensions.Primitives;

namespace CtxUi.Builder.Runner.Tests;

public sealed class RouteTableTests
{
    private static readonly Guid Id = RunnerHarness.Id;

    private static RouteTable Table()
    {
        var probe = new FakeServerProbe();
        probe.Route("one", "http://one:8000");
        probe.Route("two", "http://two:8000");
        return new RouteTable(probe);
    }

    [Fact]
    public void Active_route_goes_to_port_8000_of_the_container_at_mcp_and_strips_the_secret()
    {
        var table = Table();
        table.SetActive(Id, "one", "abc");

        var config = table.GetConfig();
        var route = Assert.Single(config.Routes);
        var cluster = Assert.Single(config.Clusters);

        Assert.Equal($"/s/{Id}/mcp", route.Match.Path);
        Assert.Equal("abc", route.Metadata![RouteTable.SecretHashKey]);
        Assert.Equal("http://one:8000", cluster.Destinations!.Values.Single().Address);
        Assert.Contains(route.Transforms!, transform => transform.TryGetValue("PathSet", out var path) && path == "/mcp");
        Assert.Contains(route.Transforms!, transform => transform.TryGetValue("RequestHeaderRemove", out var header) && header == RouteSecret.Header);
    }

    [Fact]
    public void Long_replies_have_no_activity_timeout_and_no_buffering()
    {
        var table = Table();
        table.SetActive(Id, "one", "abc");

        var request = Assert.Single(table.GetConfig().Clusters).HttpRequest!;

        Assert.Equal(Timeout.InfiniteTimeSpan, request.ActivityTimeout);
        Assert.False(request.AllowResponseBuffering);
    }

    [Fact]
    public void Candidate_route_has_its_own_path_and_secret()
    {
        var table = Table();
        table.SetActive(Id, "one", "abc");
        table.SetCandidate(Id, 2, "two", "def");

        var routes = table.GetConfig().Routes;

        Assert.Equal(2, routes.Count);
        var candidate = Assert.Single(routes, route => route.Match.Path == $"/s/{Id}/r/2/mcp");
        Assert.Equal("def", candidate.Metadata![RouteTable.SecretHashKey]);
    }

    [Fact]
    public void A_change_signals_the_old_token_and_leaves_the_old_snapshot_whole()
    {
        var table = Table();
        table.SetActive(Id, "one", "abc");
        var before = table.GetConfig();
        IChangeToken token = before.ChangeToken;
        Assert.False(token.HasChanged);

        table.SetActive(Id, "two", "def");

        Assert.True(token.HasChanged);
        Assert.Equal("http://one:8000", Assert.Single(before.Clusters).Destinations!.Values.Single().Address);
        Assert.Equal("http://two:8000", Assert.Single(table.GetConfig().Clusters).Destinations!.Values.Single().Address);
    }

    [Fact]
    public void Removing_a_server_removes_all_its_routes_and_no_others()
    {
        var other = Guid.NewGuid();
        var table = Table();
        table.SetActive(Id, "one", "abc");
        table.SetCandidate(Id, 2, "two", "def");
        table.SetActive(other, "two", "ghi");

        table.RemoveServer(Id);

        var route = Assert.Single(table.GetConfig().Routes);
        Assert.Equal($"/s/{other}/mcp", route.Match.Path);
        Assert.Single(table.GetConfig().Clusters);
    }

    [Fact]
    public void Removing_a_candidate_leaves_the_active_route()
    {
        var table = Table();
        table.SetActive(Id, "one", "abc");
        table.SetCandidate(Id, 2, "two", "def");

        table.RemoveCandidate(Id, 2);

        Assert.Equal($"/s/{Id}/mcp", Assert.Single(table.GetConfig().Routes).Match.Path);
    }
}
