using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class ClientLinkServiceTests
{
    private static readonly ExternalIdentity Ivanov = new("google", "user-1", "ivanov@example.org");

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private ClientLinkService Links(TestDatabase database) => new(database.NewContext(), _clock);

    private Guid _ivanov;
    private Guid _petrov;

    private async Task<TestDatabase> TwoClientsAsync()
    {
        var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        _ivanov = (await new ClientService(database.NewContext(), _vault, _clock).AddAsync("Иванов", ["echo"])).Client.Id;
        _petrov = (await new ClientService(database.NewContext(), _vault, _clock).AddAsync("Петров", ["echo"])).Client.Id;
        return database;
    }

    [Fact]
    public async Task Invite_code_is_stored_as_a_hash()
    {
        await using var database = await TwoClientsAsync();

        var code = await Links(database).InviteAsync(_ivanov);

        Assert.True(InviteCode.IsWellFormed(code));
        var client = await database.NewContext().Clients.AsNoTracking().SingleAsync(saved => saved.Id == _ivanov);
        Assert.Equal(InviteCode.Hash(code), client.InviteHash);
        Assert.Equal(TestData.Now.AddDays(7), client.InviteExpiresAt);
        Assert.Equal(_ivanov, await Links(database).InvitedClientAsync(code));
    }

    [Fact]
    public async Task Invite_works_once()
    {
        await using var database = await TwoClientsAsync();
        var code = await Links(database).InviteAsync(_ivanov);

        var first = await Links(database).LinkAsync(code, Ivanov);
        var second = await Links(database).LinkAsync(code, Ivanov with { Subject = "user-2" });

        Assert.Equal(new LinkResult(LinkOutcome.Linked, _ivanov), first);
        Assert.Equal(LinkOutcome.InviteInvalid, second.Outcome);
        Assert.Null(await Links(database).InvitedClientAsync(code));
        var link = Assert.Single(await Links(database).ListAsync(_ivanov));
        Assert.Equal(("google", "user-1", "ivanov@example.org", TestData.Now), (link.Provider, link.Subject, link.Display, link.LinkedAt));
    }

    [Fact]
    public async Task Invite_expires_after_seven_days()
    {
        await using var database = await TwoClientsAsync();
        var code = await Links(database).InviteAsync(_ivanov);

        _clock.Advance(TimeSpan.FromDays(7));

        Assert.Null(await Links(database).InvitedClientAsync(code));
        Assert.Equal(LinkOutcome.InviteInvalid, (await Links(database).LinkAsync(code, Ivanov)).Outcome);
    }

    [Fact]
    public async Task New_invite_cancels_the_previous_one()
    {
        await using var database = await TwoClientsAsync();
        var old = await Links(database).InviteAsync(_ivanov);

        var fresh = await Links(database).InviteAsync(_ivanov);

        Assert.Null(await Links(database).InvitedClientAsync(old));
        Assert.Equal(_ivanov, await Links(database).InvitedClientAsync(fresh));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("inv_short")]
    [InlineData("tnk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Malformed_invite_is_invalid(string? code)
    {
        await using var database = await TwoClientsAsync();

        Assert.Null(await Links(database).InvitedClientAsync(code!));
        Assert.Equal(LinkOutcome.InviteInvalid, (await Links(database).LinkAsync(code!, Ivanov)).Outcome);
    }

    [Fact]
    public async Task Account_linked_to_another_client_is_refused()
    {
        await using var database = await TwoClientsAsync();
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), Ivanov);
        var code = await Links(database).InviteAsync(_petrov);

        var result = await Links(database).LinkAsync(code, Ivanov);

        Assert.Equal(new LinkResult(LinkOutcome.TakenByOtherClient, _ivanov), result);
        Assert.Equal(_petrov, await Links(database).InvitedClientAsync(code));
        Assert.Empty(await Links(database).ListAsync(_petrov));
    }

    [Fact]
    public async Task Linking_the_same_account_again_is_already_linked()
    {
        await using var database = await TwoClientsAsync();
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), Ivanov);
        var code = await Links(database).InviteAsync(_ivanov);

        var result = await Links(database).LinkAsync(code, Ivanov);

        Assert.Equal(new LinkResult(LinkOutcome.AlreadyLinked, _ivanov), result);
        Assert.Single(await Links(database).ListAsync(_ivanov));
        Assert.Null(await Links(database).InvitedClientAsync(code));
    }

    [Fact]
    public async Task Linked_account_finds_its_client()
    {
        await using var database = await TwoClientsAsync();
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), Ivanov);

        Assert.Equal(_ivanov, await Links(database).ClientOfAsync(Ivanov with { Display = "renamed@example.org" }));
        Assert.Null(await Links(database).ClientOfAsync(Ivanov with { Provider = "github" }));
    }

    [Fact]
    public async Task Unlink_removes_every_link_of_that_provider()
    {
        await using var database = await TwoClientsAsync();
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), Ivanov);
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), Ivanov with { Subject = "user-2" });
        await Links(database).LinkAsync(await Links(database).InviteAsync(_ivanov), new ExternalIdentity("github", "1", "ivanov"));

        await Links(database).UnlinkAsync(_ivanov, "Google");

        Assert.Equal("github", Assert.Single(await Links(database).ListAsync(_ivanov)).Provider);
        await Assert.ThrowsAsync<NotFoundException>(() => Links(database).UnlinkAsync(_ivanov, "google"));
    }

    [Fact]
    public async Task Inviting_an_unknown_client_is_not_found()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Links(database).InviteAsync(Guid.NewGuid()));
    }
}
