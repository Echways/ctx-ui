using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CtxUi.Server;

namespace CtxUi.IntegrationTests;

public sealed partial class CliBackupTests : IDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ctx-ui-cli-").FullName;
    private readonly string _backupRoot = Directory.CreateTempSubdirectory("ctx-ui-backup-").FullName;

    [GeneratedRegex("tnk_[A-Za-z0-9]{32}")]
    private static partial Regex TokenPattern();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var directory in new[] { _dataDir, _backupRoot })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Backup_is_a_cli_command() => Assert.True(Cli.Handles(["backup", "/tmp/copy"]));

    [Fact]
    public async Task Backup_holds_the_database_and_keys_and_restores_into_a_working_installation()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");
        var added = await CliRunner.RunAsync(_dataDir, "client", "add", "Иванов", "--inbound", "echo");
        var token = TokenPattern().Match(added.Output).Value;
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);
        var restored = await CliRunner.RunAsync(target, "client", "show", "Иванов");

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        Assert.NotEmpty(token);
        Assert.Equal((0, ""), (restored.Code, restored.Error));
        Assert.Contains(token, restored.Output);
    }

    [Fact]
    public async Task Backup_refuses_a_directory_that_is_not_empty()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "add", "echo", "--url", "https://upstream.example.org/mcp");
        var keep = Path.Combine(_backupRoot, "keep.txt");
        await File.WriteAllTextAsync(keep, "keep");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", _backupRoot);

        Assert.Equal(1, backup.Code);
        Assert.Contains("не пуст", backup.Error);
        Assert.Equal(["keep.txt"], Directory.EnumerateFileSystemEntries(_backupRoot).Select(Path.GetFileName));
        Assert.Equal("keep", await File.ReadAllTextAsync(keep));
    }
}
