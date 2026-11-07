using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core;
using CtxUi.Core.Data;
using CtxUi.Server.Extensions;

namespace CtxUi.Server;

public static class Backup
{
    private const int NotADatabase = 26;

    private static readonly string[] DatabaseSidecars = [".db-wal", ".db-shm", ".db-journal"];

    public static async Task WriteAsync(CtxUiDbContext db, string dataDir, string extensionsDir, string target,
        CancellationToken ct)
    {
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new DomainException(string.Format(ServerText.BackupTargetNotEmpty, target));

        ServiceRegistration.CreatePrivateDirectory(target);
        var database = Path.Combine(target, CoreServices.DatabaseFile);
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {database}", ct);

        var keys = Path.Combine(target, ServiceRegistration.KeysDirectory);
        ServiceRegistration.CreatePrivateDirectory(keys);
        foreach (var key in Directory.EnumerateFiles(Path.Combine(dataDir, ServiceRegistration.KeysDirectory), "*.xml"))
            File.Copy(key, Path.Combine(keys, Path.GetFileName(key)));

        if (Directory.Exists(extensionsDir))
            CopyExtensions(extensionsDir, Path.Combine(target, ExtensionCatalog.DefaultFolder));
        var extensionData = Path.Combine(dataDir, ExtensionCatalog.DataFolder);
        if (Directory.Exists(extensionData))
            await CopyDataAsync(extensionData, Path.Combine(target, ExtensionCatalog.DataFolder), ct);
    }

    private static void CopyExtensions(string from, string to)
    {
        ServiceRegistration.CreatePrivateDirectory(to);
        foreach (var extension in Directory.EnumerateDirectories(from)
                     .Where(directory => !Path.GetFileName(directory).StartsWith('.')))
            CopyTree(extension, Path.Combine(to, Path.GetFileName(extension)));
    }

    private static void CopyTree(string from, string to)
    {
        ServiceRegistration.CreatePrivateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(from))
            CopyTree(directory, Path.Combine(to, Path.GetFileName(directory)));
    }

    private static async Task CopyDataAsync(string from, string to, CancellationToken ct)
    {
        ServiceRegistration.CreatePrivateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var copy = Path.Combine(to, Path.GetFileName(file));
            if (DatabaseSidecars.Any(sidecar => file.EndsWith(sidecar, StringComparison.Ordinal)))
                continue;
            if (file.EndsWith(".db", StringComparison.Ordinal))
                await CopyDatabaseAsync(file, copy, ct);
            else
                File.Copy(file, copy);
        }

        foreach (var directory in Directory.EnumerateDirectories(from))
            await CopyDataAsync(directory, Path.Combine(to, Path.GetFileName(directory)), ct);
    }

    private static async Task CopyDatabaseAsync(string from, string to, CancellationToken ct)
    {
        await using var connection =
            new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = from, Pooling = false }.ToString());
        await connection.OpenAsync(ct);
        await using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM INTO @copy";
        vacuum.Parameters.AddWithValue("@copy", to);
        try
        {
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException failure) when (failure.SqliteErrorCode == NotADatabase)
        {
            File.Copy(from, to);
        }
    }
}
