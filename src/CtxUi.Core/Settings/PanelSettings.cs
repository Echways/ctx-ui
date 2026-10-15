using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;

namespace CtxUi.Core.Settings;

public sealed class PanelSettings(CtxUiDbContext db)
{
    public const string PathKey = "panel.path";
    public const int PathLength = 16;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<string> PathAsync(CancellationToken ct = default)
    {
        var candidate = NewPath();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Settings" ("Key", "Value") VALUES ({PathKey}, {candidate})
            ON CONFLICT ("Key") DO NOTHING
            """,
            ct);
        return await db.Settings.AsNoTracking()
            .Where(setting => setting.Key == PathKey)
            .Select(setting => setting.Value)
            .SingleAsync(ct);
    }

    public async Task<string> RenewPathAsync(CancellationToken ct = default)
    {
        var path = NewPath();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "Settings" ("Key", "Value") VALUES ({PathKey}, {path})
            ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value"
            """,
            ct);
        return path;
    }

    private static string NewPath() => RandomNumberGenerator.GetString(Alphabet, PathLength);
}
