using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed partial class CliAdminTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;

    [GeneratedRegex(@"Пароль:  ([A-Za-z0-9]{20})\b")]
    private static partial Regex PasswordLine();

    [GeneratedRegex(@"Панель:  (https://mcp\.example\.org/[a-z0-9]{16}/)")]
    private static partial Regex PanelLine();

    private Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => CliRunner.RunAsync(_dataDir, args);

    private static string Password(string output) => PasswordLine().Match(output).Groups[1].Value;

    private static string Panel(string output) => PanelLine().Match(output).Groups[1].Value;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Admin_is_a_cli_command() => Assert.True(Cli.Handles(["admin", "list"]));

    [Fact]
    public async Task Added_admin_gets_a_generated_password_and_the_panel_address()
    {
        var added = await RunAsync("admin", "add", "root");

        Assert.Equal(0, added.Code);
        Assert.Equal("", added.Error);
        Assert.Contains("Логин:   root", added.Output);
        Assert.Matches(PasswordLine(), added.Output);
        Assert.Matches(PanelLine(), added.Output);
    }

    [Fact]
    public async Task Password_is_kept_only_as_a_hash_that_it_matches()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var added = await CliRunner.RunAsync(gateway.DataDir, "admin", "add", "root");
        var password = Password(added.Output);

        var (admin, verdict) = await gateway.ChangeAsync(async services =>
        {
            var stored = await services.GetRequiredService<CtxUiDbContext>().Admins.SingleAsync();
            var hasher = services.GetRequiredService<IPasswordHasher<Admin>>();
            return (stored, hasher.VerifyHashedPassword(stored, stored.PasswordHash, password));
        });

        Assert.Equal(PasswordVerificationResult.Success, verdict);
        Assert.DoesNotContain(password, admin.PasswordHash);
        Assert.Equal("ROOT", admin.NameKey);
        Assert.NotEqual("", admin.SecurityStamp);
    }

    [Fact]
    public async Task Every_admin_gets_a_password_of_their_own()
    {
        var first = await RunAsync("admin", "add", "root");
        var second = await RunAsync("admin", "add", "second");

        Assert.NotEqual(Password(first.Output), Password(second.Output));
        Assert.Equal(Panel(first.Output), Panel(second.Output));
    }

    [Theory]
    [InlineData("Root", "уже есть")]
    [InlineData("два слова", "логин")]
    [InlineData("иван", "логин")]
    [InlineData("", "логин")]
    public async Task Login_that_is_taken_or_breaks_the_rules_is_refused(string login, string because)
    {
        await RunAsync("admin", "add", "root");

        var refused = await RunAsync("admin", "add", login);
        var listed = await RunAsync("admin", "list");

        Assert.Equal(1, refused.Code);
        Assert.StartsWith("Ошибка: ", refused.Error);
        Assert.Contains(because, refused.Error);
        Assert.Equal("", refused.Output);
        Assert.Single(listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task New_password_replaces_the_old_one_and_lifts_the_lockout()
    {
        await using var gateway = await GatewayHost.StartAsync();
        var old = Password((await CliRunner.RunAsync(gateway.DataDir, "admin", "add", "root")).Output);
        var stampBefore = await gateway.ChangeAsync(async services =>
        {
            var db = services.GetRequiredService<CtxUiDbContext>();
            var admin = await db.Admins.SingleAsync();
            admin.FailedAttempts = 4;
            admin.LockedUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            await db.SaveChangesAsync();
            return admin.SecurityStamp;
        });

        var changed = await CliRunner.RunAsync(gateway.DataDir, "admin", "password", "ROOT");

        var fresh = Password(changed.Output);
        var (admin, oldVerdict, freshVerdict) = await gateway.ChangeAsync(async services =>
        {
            var stored = await services.GetRequiredService<CtxUiDbContext>().Admins.SingleAsync();
            var hasher = services.GetRequiredService<IPasswordHasher<Admin>>();
            return (
                stored,
                hasher.VerifyHashedPassword(stored, stored.PasswordHash, old),
                hasher.VerifyHashedPassword(stored, stored.PasswordHash, fresh));
        });
        Assert.Equal(0, changed.Code);
        Assert.Contains("Логин:   root", changed.Output);
        Assert.Matches(PanelLine(), changed.Output);
        Assert.NotEqual(old, fresh);
        Assert.Equal(PasswordVerificationResult.Failed, oldVerdict);
        Assert.Equal(PasswordVerificationResult.Success, freshVerdict);
        Assert.Equal(0, admin.FailedAttempts);
        Assert.Null(admin.LockedUntil);
        Assert.NotEqual(stampBefore, admin.SecurityStamp);
    }

    [Fact]
    public async Task List_names_the_admins_and_says_so_when_there_are_none()
    {
        var empty = await RunAsync("admin", "list");
        await RunAsync("admin", "add", "root");
        await RunAsync("admin", "add", "anna");

        var listed = await RunAsync("admin", "list");

        Assert.Contains("Администраторов нет", empty.Output);
        Assert.Contains("ctx-ui admin add", empty.Output);
        var lines = listed.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("anna", lines[0]);
        Assert.StartsWith("root", lines[1]);
        Assert.DoesNotContain("Пароль", listed.Output);
    }

    [Fact]
    public async Task Removed_admin_is_gone_and_an_unknown_one_is_reported()
    {
        await RunAsync("admin", "add", "root");

        var removed = await RunAsync("admin", "remove", "root");
        var again = await RunAsync("admin", "remove", "root");
        var password = await RunAsync("admin", "password", "root");
        var listed = await RunAsync("admin", "list");

        Assert.Equal(0, removed.Code);
        Assert.Contains("удалён", removed.Output);
        Assert.Equal(1, again.Code);
        Assert.Contains("администратор «root» не найден", again.Error);
        Assert.Equal(1, password.Code);
        Assert.Contains("Администраторов нет", listed.Output);
    }

    [Fact]
    public async Task Panel_address_stays_until_it_is_renewed()
    {
        var added = await RunAsync("admin", "add", "root");
        var shown = await RunAsync("admin", "path");

        var renewed = await RunAsync("admin", "path", "--new");
        var after = await RunAsync("admin", "path");

        Assert.Equal(Panel(added.Output), Panel(shown.Output));
        Assert.Equal(0, renewed.Code);
        Assert.NotEqual(Panel(shown.Output), Panel(renewed.Output));
        Assert.Contains("перезапуск", renewed.Output);
        Assert.Equal(Panel(renewed.Output), Panel(after.Output));
        Assert.DoesNotContain("перезапуск", after.Output);
    }
}
