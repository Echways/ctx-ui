using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Settings;

namespace CtxUi.Core.Tests;

public class PanelSettingsTests
{
    private static PanelSettings Settings(TestDatabase database) => new(database.NewContext());

    [Fact]
    public async Task Path_is_made_on_first_read_and_then_stays()
    {
        await using var database = await TestDatabase.CreateAsync();

        var first = await Settings(database).PathAsync();
        var second = await Settings(database).PathAsync();

        Assert.Matches(new Regex(@"\A[a-z0-9]{16}\z"), first);
        Assert.Equal(first, second);
        await using var db = database.NewContext();
        Assert.Equal(first, (await db.Settings.SingleAsync()).Value);
        Assert.Equal(0, (await db.RoutingStates.SingleAsync()).Revision);
    }

    [Fact]
    public async Task Every_installation_gets_its_own_path()
    {
        await using var one = await TestDatabase.CreateAsync();
        await using var other = await TestDatabase.CreateAsync();

        Assert.NotEqual(await Settings(one).PathAsync(), await Settings(other).PathAsync());
    }

    [Fact]
    public async Task Path_written_by_another_process_first_wins()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            db.Settings.Add(new Setting { Key = PanelSettings.PathKey, Value = "writtenbyanother1" });
            await db.SaveChangesAsync();
        }

        Assert.Equal("writtenbyanother1", await Settings(database).PathAsync());
    }

    [Fact]
    public async Task Renewed_path_replaces_the_old_one()
    {
        await using var database = await TestDatabase.CreateAsync();
        var old = await Settings(database).PathAsync();

        var renewed = await Settings(database).RenewPathAsync();

        Assert.Matches(new Regex(@"\A[a-z0-9]{16}\z"), renewed);
        Assert.NotEqual(old, renewed);
        Assert.Equal(renewed, await Settings(database).PathAsync());
    }

    [Fact]
    public async Task Path_can_be_renewed_before_it_was_ever_read()
    {
        await using var database = await TestDatabase.CreateAsync();

        var renewed = await Settings(database).RenewPathAsync();

        Assert.Equal(renewed, await Settings(database).PathAsync());
    }
}
