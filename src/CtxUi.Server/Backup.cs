using Microsoft.EntityFrameworkCore;
using CtxUi.Core;
using CtxUi.Core.Data;

namespace CtxUi.Server;

public static class Backup
{
    public static async Task WriteAsync(CtxUiDbContext db, string dataDir, string target, CancellationToken ct)
    {
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new DomainException($"каталог {target} не пуст, нужен новый или пустой");

        ServiceRegistration.CreatePrivateDirectory(target);
        var database = Path.Combine(target, CoreServices.DatabaseFile);
        await db.Database.ExecuteSqlAsync($"VACUUM INTO {database}", ct);

        var keys = Path.Combine(target, ServiceRegistration.KeysDirectory);
        ServiceRegistration.CreatePrivateDirectory(keys);
        foreach (var key in Directory.EnumerateFiles(Path.Combine(dataDir, ServiceRegistration.KeysDirectory), "*.xml"))
            File.Copy(key, Path.Combine(keys, Path.GetFileName(key)));
    }
}
