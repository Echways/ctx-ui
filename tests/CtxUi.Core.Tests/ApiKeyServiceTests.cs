using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using CtxUi.Core.ApiKeys;

namespace CtxUi.Core.Tests;

public class ApiKeyServiceTests
{
    private static ApiKeyService Keys(TestDatabase database) => new(database.NewContext(), new FakeTimeProvider(TestData.Now));

    [Fact]
    public async Task Issued_key_is_stored_only_as_hash_and_prefix()
    {
        await using var database = await TestDatabase.CreateAsync();

        var issued = await Keys(database).AddAsync(" bot ");

        Assert.Matches(@"\Actxk_[A-Za-z0-9]{40}\z", issued.Secret);
        await using var db = database.NewContext();
        var stored = await db.ApiKeys.SingleAsync();
        Assert.Equal("bot", stored.Name);
        Assert.Equal("BOT", stored.NameKey);
        Assert.Equal(ApiKeySecret.Hash(issued.Secret), stored.SecretHash);
        Assert.Equal(issued.Secret[..11], stored.SecretPrefix);
        Assert.Equal(TestData.Now, stored.CreatedAt);
        Assert.DoesNotContain(issued.Secret, stored.SecretHash);
    }

    [Fact]
    public async Task Each_key_is_different()
    {
        await using var database = await TestDatabase.CreateAsync();

        var first = await Keys(database).AddAsync("first");
        var second = await Keys(database).AddAsync("second");

        Assert.NotEqual(first.Secret, second.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("телеграм")]
    [InlineData("two words")]
    [InlineData("a/b")]
    public async Task Name_outside_the_alphabet_is_refused(string name)
    {
        await using var database = await TestDatabase.CreateAsync();

        var error = await Assert.ThrowsAsync<DomainException>(() => Keys(database).AddAsync(name));

        Assert.Contains("название ключа", error.Message);
    }

    [Fact]
    public async Task Name_longer_than_64_is_refused()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Assert.ThrowsAsync<DomainException>(() => Keys(database).AddAsync(new string('a', 65)));
        await Keys(database).AddAsync(new string('a', 64));
    }

    [Fact]
    public async Task Second_key_with_the_same_name_in_any_case_is_refused()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Keys(database).AddAsync("bot");

        var error = await Assert.ThrowsAsync<DomainException>(() => Keys(database).AddAsync("BOT"));

        Assert.Contains("уже есть", error.Message);
    }

    [Fact]
    public async Task Keys_are_listed_by_name()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Keys(database).AddAsync("zeta");
        await Keys(database).AddAsync("Alpha");

        Assert.Equal(["Alpha", "zeta"], (await Keys(database).ListAsync()).Select(key => key.Name));
    }

    [Fact]
    public async Task Key_is_found_by_its_secret_only()
    {
        await using var database = await TestDatabase.CreateAsync();
        var issued = await Keys(database).AddAsync("bot");

        Assert.Equal("bot", (await Keys(database).FindAsync(issued.Secret))?.Name);
        Assert.Null(await Keys(database).FindAsync(issued.Secret[..^1] + (issued.Secret[^1] == 'a' ? 'b' : 'a')));
        Assert.Null(await Keys(database).FindAsync(null));
        Assert.Null(await Keys(database).FindAsync(""));
        Assert.Null(await Keys(database).FindAsync("tnk_" + issued.Secret[5..]));
    }

    [Fact]
    public async Task Removed_key_is_no_longer_found()
    {
        await using var database = await TestDatabase.CreateAsync();
        var issued = await Keys(database).AddAsync("bot");

        await Keys(database).RemoveAsync(" BOT ");

        Assert.Null(await Keys(database).FindAsync(issued.Secret));
        Assert.Empty(await Keys(database).ListAsync());
    }

    [Fact]
    public async Task Removing_an_unknown_key_is_reported()
    {
        await using var database = await TestDatabase.CreateAsync();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => Keys(database).RemoveAsync("nope"));

        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public async Task Keys_do_not_touch_the_routing_revision()
    {
        await using var database = await TestDatabase.CreateAsync();

        await Keys(database).AddAsync("bot");

        await using var db = database.NewContext();
        Assert.Equal(0, await db.RoutingStates.Select(state => state.Revision).SingleAsync());
    }
}
