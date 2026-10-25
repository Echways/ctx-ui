using System.Collections.Frozen;
using CtxUi.Core.Clients;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class RoutingSnapshotTests
{
    private const string Token = "tnk_0123456789abcdefghijABCDEFGHIJ01";
    private const string OtherToken = "tnk_0123456789abcdefghijABCDEFGHIJ02";

    private static readonly InboundRoute Echo = new(
        Guid.NewGuid(), "echo", new Uri("http://upstream.test/mcp"), new Dictionary<string, string>(), false, TimeSpan.FromSeconds(300));

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid DeviceId = Guid.NewGuid();

    private static RoutingSnapshot Snapshot(
        bool enabled = true,
        DateTimeOffset? expiresAt = null,
        bool granted = true,
        bool directKey = true,
        bool deviceActive = true)
    {
        Guid[] inboundIds = granted ? [Echo.Id] : [];
        var client = new ClientAccess(
            ClientId, "Иванов", enabled, expiresAt, inboundIds.ToFrozenSet(), new Dictionary<string, string>())
        {
            DirectKey = directKey,
        };
        KeyValuePair<Guid, Guid>[] devices = deviceActive ? [KeyValuePair.Create(DeviceId, ClientId)] : [];
        return new RoutingSnapshot(7, [Echo], [KeyValuePair.Create(AccessToken.Hash(Token), client)], devices, SignInConfig.Off);
    }

    private static Credential Key(string? token) => Credential.FromKey(token);

    private static Credential Grant(Guid? clientId = null) =>
        new(CredentialKind.Grant, Grant: new OAuthGrant(clientId ?? ClientId, DeviceId, ["https://mcp.example.org/mcp/echo"]));

    [Fact]
    public void Known_token_with_access_is_allowed()
    {
        var decision = Snapshot().Authorize(Key(Token), "echo", TestData.Now);

        Assert.Equal(AccessStatus.Allowed, decision.Status);
        Assert.Equal("Иванов", decision.Client!.Name);
        Assert.Equal(Echo, decision.Route);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Absent_token_is_reported_as_missing(string? token) =>
        Assert.Equal(AccessStatus.TokenMissing, Snapshot().Authorize(Key(token), "echo", TestData.Now).Status);

    [Theory]
    [InlineData("garbage")]
    [InlineData(OtherToken)]
    public void Malformed_or_foreign_token_is_unknown(string token)
    {
        var decision = Snapshot().Authorize(Key(token), "echo", TestData.Now);

        Assert.Equal(AccessStatus.TokenUnknown, decision.Status);
        Assert.Null(decision.Client);
    }

    [Fact]
    public void Disabled_client_is_rejected_before_looking_at_the_inbound() =>
        Assert.Equal(AccessStatus.ClientDisabled, Snapshot(enabled: false).Authorize(Key(Token), "nope", TestData.Now).Status);

    [Fact]
    public void Client_expires_exactly_at_the_expiry_moment()
    {
        var snapshot = Snapshot(expiresAt: TestData.Now);

        Assert.Equal(AccessStatus.Allowed, snapshot.Authorize(Key(Token), "echo", TestData.Now.AddMilliseconds(-1)).Status);
        var decision = snapshot.Authorize(Key(Token), "echo", TestData.Now);
        Assert.Equal(AccessStatus.ClientExpired, decision.Status);
        Assert.Equal(TestData.Now, decision.Client!.ExpiresAt);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Echo")]
    public void Unknown_slug_is_not_found(string slug) =>
        Assert.Equal(AccessStatus.InboundNotFound, Snapshot().Authorize(Key(Token), slug, TestData.Now).Status);

    [Fact]
    public void Inbound_not_granted_to_the_client_looks_like_not_found() =>
        Assert.Equal(AccessStatus.InboundNotFound, Snapshot(granted: false).Authorize(Key(Token), "echo", TestData.Now).Status);

    [Fact]
    public void Empty_snapshot_knows_nobody_and_has_a_revision_no_database_can_have()
    {
        Assert.Equal(-1, RoutingSnapshot.Empty.Revision);
        Assert.Equal(AccessStatus.TokenUnknown, RoutingSnapshot.Empty.Authorize(Key(Token), "echo", TestData.Now).Status);
    }

    [Fact]
    public void Direct_key_is_refused_when_the_client_has_it_off()
    {
        var decision = Snapshot(directKey: false, enabled: false).Authorize(Key(Token), "echo", TestData.Now);

        Assert.Equal(AccessStatus.DirectKeyOff, decision.Status);
        Assert.Equal(ClientId, decision.Client!.Id);
    }

    [Fact]
    public void Grant_for_an_active_device_is_allowed_and_names_the_device()
    {
        var decision = Snapshot(directKey: false).Authorize(Grant(), "echo", TestData.Now);

        Assert.Equal(AccessStatus.Allowed, decision.Status);
        Assert.Equal(DeviceId, decision.DeviceId);
        Assert.Equal(Echo, decision.Route);
    }

    [Fact]
    public void Grant_for_a_revoked_device_is_device_revoked() =>
        Assert.Equal(AccessStatus.DeviceRevoked, Snapshot(deviceActive: false).Authorize(Grant(), "echo", TestData.Now).Status);

    [Fact]
    public void Grant_of_an_unknown_client_is_token_invalid() =>
        Assert.Equal(AccessStatus.TokenInvalid, Snapshot().Authorize(Grant(Guid.NewGuid()), "echo", TestData.Now).Status);

    [Fact]
    public void Grant_of_a_disabled_client_is_client_disabled() =>
        Assert.Equal(AccessStatus.ClientDisabled, Snapshot(enabled: false).Authorize(Grant(), "echo", TestData.Now).Status);

    [Fact]
    public void Grant_for_an_inbound_outside_the_client_is_inbound_not_found() =>
        Assert.Equal(AccessStatus.InboundNotFound, Snapshot(granted: false).Authorize(Grant(), "echo", TestData.Now).Status);

    [Fact]
    public void Invalid_credential_is_token_invalid() =>
        Assert.Equal(AccessStatus.TokenInvalid, Snapshot().Authorize(new Credential(CredentialKind.Invalid), "echo", TestData.Now).Status);

    [Fact]
    public void Device_is_active_only_for_its_own_client()
    {
        Assert.True(Snapshot().IsDeviceActive(DeviceId, ClientId));
        Assert.False(Snapshot().IsDeviceActive(DeviceId, Guid.NewGuid()));
        Assert.False(Snapshot(deviceActive: false).IsDeviceActive(DeviceId, ClientId));
    }

    [Fact]
    public void Sign_in_is_active_only_when_enabled_with_an_allowed_public_url()
    {
        var on = SignInConfig.Off with { Enabled = true };

        Assert.True(on.IsActive("https://mcp.example.org"));
        Assert.False(on.IsActive("http://mcp.example.org"));
        Assert.False(SignInConfig.Off.IsActive("https://mcp.example.org"));
        Assert.Same(SignInConfig.Off, RoutingSnapshot.Empty.SignIn);
    }

    [Fact]
    public void Route_is_found_by_its_exact_slug()
    {
        Assert.Equal(Echo, Snapshot().Route("echo"));
        Assert.Null(Snapshot().Route("Echo"));
    }
}
