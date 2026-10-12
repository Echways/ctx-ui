using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;

namespace CtxUi.Core.Tests;

public class RoutingSnapshotTests
{
    private const string Token = "tnk_0123456789abcdefghijABCDEFGHIJ01";
    private const string OtherToken = "tnk_0123456789abcdefghijABCDEFGHIJ02";

    private static readonly InboundRoute Echo = new(
        Guid.NewGuid(), "echo", new Uri("http://upstream.test/mcp"), new Dictionary<string, string>(), false, TimeSpan.FromSeconds(300));

    private static RoutingSnapshot Snapshot(bool enabled = true, DateTimeOffset? expiresAt = null, bool granted = true)
    {
        Guid[] inboundIds = granted ? [Echo.Id] : [];
        var client = new ClientAccess(
            Guid.NewGuid(), "Иванов", enabled, expiresAt, inboundIds.ToFrozenSet(), new Dictionary<string, string>());
        return new RoutingSnapshot(7, [Echo], [KeyValuePair.Create(AccessToken.Hash(Token), client)]);
    }

    [Fact]
    public void Known_token_with_access_is_allowed()
    {
        var decision = Snapshot().Authorize(Token, "echo", TestData.Now);

        Assert.Equal(AccessStatus.Allowed, decision.Status);
        Assert.Equal("Иванов", decision.Client!.Name);
        Assert.Equal(Echo, decision.Route);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Absent_token_is_reported_as_missing(string? token) =>
        Assert.Equal(AccessStatus.TokenMissing, Snapshot().Authorize(token, "echo", TestData.Now).Status);

    [Theory]
    [InlineData("garbage")]
    [InlineData(OtherToken)]
    public void Malformed_or_foreign_token_is_unknown(string token)
    {
        var decision = Snapshot().Authorize(token, "echo", TestData.Now);

        Assert.Equal(AccessStatus.TokenUnknown, decision.Status);
        Assert.Null(decision.Client);
    }

    [Fact]
    public void Disabled_client_is_rejected_before_looking_at_the_inbound() =>
        Assert.Equal(AccessStatus.ClientDisabled, Snapshot(enabled: false).Authorize(Token, "nope", TestData.Now).Status);

    [Fact]
    public void Client_expires_exactly_at_the_expiry_moment()
    {
        var snapshot = Snapshot(expiresAt: TestData.Now);

        Assert.Equal(AccessStatus.Allowed, snapshot.Authorize(Token, "echo", TestData.Now.AddMilliseconds(-1)).Status);
        var decision = snapshot.Authorize(Token, "echo", TestData.Now);
        Assert.Equal(AccessStatus.ClientExpired, decision.Status);
        Assert.Equal(TestData.Now, decision.Client!.ExpiresAt);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Echo")]
    public void Unknown_slug_is_not_found(string slug) =>
        Assert.Equal(AccessStatus.InboundNotFound, Snapshot().Authorize(Token, slug, TestData.Now).Status);

    [Fact]
    public void Inbound_not_granted_to_the_client_looks_like_not_found() =>
        Assert.Equal(AccessStatus.InboundNotFound, Snapshot(granted: false).Authorize(Token, "echo", TestData.Now).Status);

    [Fact]
    public void Empty_snapshot_knows_nobody_and_has_a_revision_no_database_can_have()
    {
        Assert.Equal(-1, RoutingSnapshot.Empty.Revision);
        Assert.Equal(AccessStatus.TokenUnknown, RoutingSnapshot.Empty.Authorize(Token, "echo", TestData.Now).Status);
    }
}
