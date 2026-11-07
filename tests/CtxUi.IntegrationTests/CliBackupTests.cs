using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using CtxUi.Core;
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
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
            }
    }

    [Fact]
    public void Backup_is_a_cli_command()
    {
        Assert.True(Cli.Handles(["backup", "/tmp/copy"]));
    }

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

    private static async Task<SqliteConnection> OpenWalAsync(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL");
        await ExecuteAsync(connection, "CREATE TABLE notes (id INTEGER PRIMARY KEY, text TEXT NOT NULL)");
        return connection;
    }

    private static async Task<object?> ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private string ExtensionData(params string[] path)
    {
        return Path.Combine([_dataDir, "extension-data", "sample", .. path]);
    }

    [Fact]
    public async Task A_backup_holds_a_consistent_copy_of_extension_data()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "list");
        Directory.CreateDirectory(ExtensionData());
        await using var writer = await OpenWalAsync(ExtensionData("data.db"));
        for (var row = 0; row < 500; row++)
            await ExecuteAsync(writer, $"INSERT INTO notes (text) VALUES ('{row}')");
        using var stop = new CancellationTokenSource();
        var busy = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await ExecuteAsync(writer, "INSERT INTO notes (text) VALUES ('later')");
        });
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);
        await stop.CancelAsync();
        await busy;

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        var copied = Path.Combine(target, "extension-data", "sample");
        Assert.DoesNotContain(Directory.GetFiles(copied),
            file => file.EndsWith("-wal", StringComparison.Ordinal) || file.EndsWith("-shm", StringComparison.Ordinal));
        await using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(copied, "data.db"), Pooling = false }.ToString());
        await copy.OpenAsync();
        Assert.Equal("ok", await ExecuteAsync(copy, "PRAGMA integrity_check"));
        Assert.True((long)(await ExecuteAsync(copy, "SELECT count(*) FROM notes"))! >= 500);
    }

    [Fact]
    public async Task A_backup_holds_extension_files_and_plain_data()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "list");
        var extension = Path.Combine(_dataDir, "extensions", "sample");
        Directory.CreateDirectory(Path.Combine(extension, "wwwroot"));
        await File.WriteAllTextAsync(Path.Combine(extension, "extension.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(extension, "wwwroot", "sample.css"), "a{}");
        Directory.CreateDirectory(Path.Combine(extension, "wwwroot", ".well-known"));
        await File.WriteAllTextAsync(Path.Combine(extension, "wwwroot", ".well-known", "file.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_dataDir, "extensions", ".install-leftover"));
        Directory.CreateDirectory(ExtensionData("nested"));
        await File.WriteAllTextAsync(ExtensionData("notes.txt"), "keep");
        await File.WriteAllTextAsync(ExtensionData("nested", "other.txt"), "keep too");
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        Assert.True(File.Exists(Path.Combine(target, "extensions", "sample", "extension.json")));
        Assert.True(File.Exists(Path.Combine(target, "extensions", "sample", "wwwroot", "sample.css")));
        Assert.True(File.Exists(Path.Combine(target, "extensions", "sample", "wwwroot", ".well-known", "file.txt")));
        Assert.False(Directory.Exists(Path.Combine(target, "extensions", ".install-leftover")));
        Assert.Equal("keep",
            await File.ReadAllTextAsync(Path.Combine(target, "extension-data", "sample", "notes.txt")));
        Assert.Equal("keep too",
            await File.ReadAllTextAsync(Path.Combine(target, "extension-data", "sample", "nested", "other.txt")));
    }

    [Fact]
    public async Task A_backup_leaves_out_the_journal_of_a_database_in_rollback_mode()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "list");
        Directory.CreateDirectory(ExtensionData());
        await File.WriteAllTextAsync(ExtensionData("data.db-journal"), "unfinished transaction");
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        Assert.False(File.Exists(Path.Combine(target, "extension-data", "sample", "data.db-journal")));
    }

    [Fact]
    public async Task A_db_file_that_is_not_a_database_is_copied_as_it_is()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "list");
        Directory.CreateDirectory(ExtensionData());
        await File.WriteAllTextAsync(ExtensionData("cache.db"),
            "plain text, long enough to read a header from it, plain text, plain text, plain text");
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        Assert.StartsWith("plain text",
            await File.ReadAllTextAsync(Path.Combine(target, "extension-data", "sample", "cache.db")));
    }

    [Fact]
    public async Task A_backup_without_extensions_stays_as_it_was()
    {
        await CliRunner.RunAsync(_dataDir, "inbound", "list");
        var target = Path.Combine(_backupRoot, "copy");

        var backup = await CliRunner.RunAsync(_dataDir, "backup", target);

        Assert.Equal((0, ""), (backup.Code, backup.Error));
        Assert.Equal(
            [CoreServices.DatabaseFile, "keys"],
            Directory.EnumerateFileSystemEntries(target).Select(entry => Path.GetFileName(entry))
                .Order(StringComparer.Ordinal));
    }
}
