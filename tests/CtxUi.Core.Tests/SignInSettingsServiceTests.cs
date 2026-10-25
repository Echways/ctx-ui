using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.SignIn;

namespace CtxUi.Core.Tests;

public class SignInSettingsServiceTests
{
    private const string PublicUrl = "https://mcp.example.org";

    private readonly FakeVault _vault = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);

    private SignInSettingsService Settings(TestDatabase database) => new(database.NewContext(), _vault);

    private static Task<long> RevisionAsync(TestDatabase database) =>
        database.NewContext().RoutingStates.AsNoTracking().Select(state => state.Revision).SingleAsync();

    [Fact]
    public async Task Enabling_needs_an_allowed_public_url()
    {
        await using var database = await TestDatabase.CreateAsync();

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true }, "http://mcp.example.org"));

        Assert.Contains("начинаться с https://", refused.Message);
        Assert.False((await Settings(database).GetAsync()).Enabled);
    }

    [Fact]
    public async Task Enabling_with_https_turns_oauth_on_and_bumps_the_revision()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true }, PublicUrl);

        Assert.True((await Settings(database).GetAsync()).Enabled);
        Assert.Equal(1, await RevisionAsync(database));
    }

    [Fact]
    public async Task Turning_oauth_off_works_with_any_public_url()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true }, PublicUrl);

        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = false }, "http://mcp.example.org");

        Assert.False((await Settings(database).GetAsync()).Enabled);
    }

    [Fact]
    public async Task Last_sign_in_method_cannot_be_turned_off_while_oauth_is_on()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).UpdateAsync(new SignInSettingsUpdate { KeySignIn = false }, PublicUrl);
        Assert.False((await Settings(database).GetAsync()).KeySignIn);

        var enabling = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true }, PublicUrl));
        Assert.Contains("хотя бы один способ входа", enabling.Message);

        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true, KeySignIn = true }, PublicUrl);
        var keyOff = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).UpdateAsync(new SignInSettingsUpdate { KeySignIn = false }, PublicUrl));
        Assert.Contains("хотя бы один способ входа", keyOff.Message);
        Assert.True((await Settings(database).GetAsync()).KeySignIn);
    }

    [Fact]
    public async Task Key_sign_in_can_be_turned_off_once_a_provider_is_enabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true }, PublicUrl);
        await Settings(database).SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));

        await Settings(database).UpdateAsync(new SignInSettingsUpdate { KeySignIn = false }, PublicUrl);

        Assert.False((await Settings(database).GetAsync()).KeySignIn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task Idle_days_stay_within_1_and_365(int days)
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).UpdateAsync(new SignInSettingsUpdate { IdleDays = days }, PublicUrl));
    }

    [Fact]
    public async Task Defaults_and_switches_are_saved()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Settings(database).UpdateAsync(
            new SignInSettingsUpdate
            {
                ChangeDefaultMaxDevices = true,
                DefaultMaxDevices = 3,
                DefaultDirectKey = false,
                IdleDays = 365,
                SelfRevoke = false,
            },
            PublicUrl);

        var settings = await Settings(database).GetAsync();
        Assert.Equal(3, settings.DefaultMaxDevices);
        Assert.False(settings.DefaultDirectKey);
        Assert.Equal(365, settings.IdleDays);
        Assert.False(settings.SelfRevoke);

        await Settings(database).UpdateAsync(new SignInSettingsUpdate { ChangeDefaultMaxDevices = true }, PublicUrl);
        Assert.Null((await Settings(database).GetAsync()).DefaultMaxDevices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Default_device_limit_is_between_1_and_1000(int limit)
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<DomainException>(() => Settings(database).UpdateAsync(
            new SignInSettingsUpdate { ChangeDefaultMaxDevices = true, DefaultMaxDevices = limit },
            PublicUrl));
    }

    [Fact]
    public async Task Provider_secret_is_sealed_and_kept_when_not_given()
    {
        await using var database = await TestDatabase.CreateAsync();
        var missing = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).SetProviderAsync(new ProviderSetup("google", "client-id", null)));
        Assert.Contains("нужен client secret", missing.Message);

        await Settings(database).SetProviderAsync(new ProviderSetup("google", "client-id", "secret"));
        var sealedSecret = Assert.Single(await Settings(database).ProvidersAsync()).SecretSealed;
        Assert.Equal("secret", _vault.Open(sealedSecret));
        Assert.DoesNotContain("secret", sealedSecret.Replace("sealed:", ""));

        await Settings(database).SetProviderAsync(new ProviderSetup("Google", "new-id", null));

        var provider = Assert.Single(await Settings(database).ProvidersAsync());
        Assert.Equal("google", provider.Name);
        Assert.Equal("new-id", provider.ClientId);
        Assert.Equal(sealedSecret, provider.SecretSealed);
        Assert.True(provider.Enabled);
    }

    [Fact]
    public async Task Provider_needs_a_client_id()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).SetProviderAsync(new ProviderSetup("google", " ", "secret")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://sso.example.org/realms/main")]
    [InlineData("sso.example.org")]
    public async Task Oidc_provider_needs_an_https_issuer(string? issuer)
    {
        await using var database = await TestDatabase.CreateAsync();

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).SetProviderAsync(new ProviderSetup("oidc", "client-id", "secret", issuer, "Keycloak")));

        Assert.Contains("адрес issuer", refused.Message);
    }

    [Fact]
    public async Task Oidc_provider_keeps_its_issuer_and_title()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Settings(database).SetProviderAsync(
            new ProviderSetup("oidc", "client-id", "secret", "https://sso.example.org/realms/main", "Keycloak"));

        var provider = Assert.Single(await Settings(database).ProvidersAsync());
        Assert.Equal("https://sso.example.org/realms/main", provider.Issuer);
        Assert.Equal("Keycloak", provider.Title);
    }

    [Fact]
    public async Task Unknown_provider_is_refused()
    {
        await using var database = await TestDatabase.CreateAsync();

        var refused = await Assert.ThrowsAsync<DomainException>(() =>
            Settings(database).SetProviderAsync(new ProviderSetup("facebook", "client-id", "secret")));

        Assert.Contains("google", refused.Message);
        Assert.Contains("facebook", refused.Message);
    }

    [Fact]
    public async Task Providers_come_in_catalog_order()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));
        await Settings(database).SetProviderAsync(new ProviderSetup("google", "client-id", "secret"));

        Assert.Equal(["google", "github"], (await Settings(database).ProvidersAsync()).Select(provider => provider.Name));
        Assert.Equal(
            ["google", "microsoft", "github", "gitlab", "yandex", "vkid", "discord", "telegram", "oidc"],
            ProviderCatalog.All.Select(provider => provider.Name));
    }

    [Fact]
    public async Task Removing_a_provider_removes_its_links()
    {
        await using var database = await TestDatabase.CreateAsync();
        await new InboundService(database.NewContext(), _vault, _clock).AddHttpAsync("echo", null, "https://mcp.example.org/mcp");
        var issued = await new ClientService(database.NewContext(), _vault, _clock).AddAsync("Иванов", ["echo"]);
        await Settings(database).SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));
        await using (var db = database.NewContext())
        {
            db.ClientLinks.Add(new ClientLink
            {
                Id = Guid.NewGuid(),
                ClientId = issued.Client.Id,
                Provider = "github",
                Subject = "1",
                Display = "ivanov",
                LinkedAt = TestData.Now,
            });
            db.ClientLinks.Add(new ClientLink
            {
                Id = Guid.NewGuid(),
                ClientId = issued.Client.Id,
                Provider = "google",
                Subject = "2",
                Display = "ivanov@example.org",
                LinkedAt = TestData.Now,
            });
            await db.SaveChangesAsync();
        }

        await Settings(database).RemoveProviderAsync("github");

        Assert.Empty(await Settings(database).ProvidersAsync());
        Assert.Equal("google", Assert.Single(await database.NewContext().ClientLinks.ToListAsync()).Provider);
    }

    [Fact]
    public async Task Removing_a_provider_that_is_not_set_is_not_found()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => Settings(database).RemoveProviderAsync("github"));
    }

    [Fact]
    public async Task Disabling_the_only_enabled_provider_with_key_off_is_refused()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));
        await Settings(database).UpdateAsync(new SignInSettingsUpdate { Enabled = true, KeySignIn = false }, PublicUrl);

        var disabling = await Assert.ThrowsAsync<DomainException>(() => Settings(database).SetProviderEnabledAsync("github", false));
        var removing = await Assert.ThrowsAsync<DomainException>(() => Settings(database).RemoveProviderAsync("github"));

        Assert.Contains("хотя бы один способ входа", disabling.Message);
        Assert.Contains("хотя бы один способ входа", removing.Message);
        Assert.True(Assert.Single(await Settings(database).ProvidersAsync()).Enabled);
    }

    [Fact]
    public async Task Provider_can_be_disabled_and_enabled_again()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Settings(database).SetProviderAsync(new ProviderSetup("github", "client-id", "secret"));

        await Settings(database).SetProviderEnabledAsync("github", false);
        Assert.False(Assert.Single(await Settings(database).ProvidersAsync()).Enabled);

        await Settings(database).SetProviderEnabledAsync("github", true);
        Assert.True(Assert.Single(await Settings(database).ProvidersAsync()).Enabled);
    }

    [Fact]
    public async Task Clients_without_the_direct_key_are_counted()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            var keyless = TestData.Client("Иванов");
            keyless.DirectKey = false;
            db.Clients.Add(keyless);
            db.Clients.Add(TestData.Client("Петров"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await Settings(database).ClientsWithoutDirectKeyAsync());
    }
}
