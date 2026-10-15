using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;

namespace CtxUi.Panel.Accounts;

public sealed record IssuedAdmin(Admin Admin, string Password);

public sealed class AdminAccounts(CtxUiDbContext db, IPasswordHasher<Admin> hasher, TimeProvider clock)
{
    public const int PasswordLength = 20;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public async Task<IssuedAdmin> AddAsync(string name, CancellationToken ct = default)
    {
        var parsed = AdminName.Parse(name);
        var key = AdminName.Key(parsed);
        if (await db.Admins.AnyAsync(known => known.NameKey == key, ct))
            throw new DomainException($"администратор «{parsed}» уже есть");

        var admin = new Admin { Id = Guid.NewGuid(), Name = parsed, NameKey = key, CreatedAt = clock.GetUtcNow() };
        var password = Renew(admin);
        db.Admins.Add(admin);
        await db.SaveChangesAsync(ct);
        return new IssuedAdmin(admin, password);
    }

    public async Task<IssuedAdmin> ResetPasswordAsync(string name, CancellationToken ct = default)
    {
        var admin = await GetAsync(name, ct);
        var password = Renew(admin);
        admin.FailedAttempts = 0;
        admin.LockedUntil = null;
        await db.SaveChangesAsync(ct);
        return new IssuedAdmin(admin, password);
    }

    public Task<List<Admin>> ListAsync(CancellationToken ct = default) =>
        db.Admins.AsNoTracking().OrderBy(admin => admin.NameKey).ToListAsync(ct);

    public async Task RemoveAsync(string name, CancellationToken ct = default)
    {
        db.Admins.Remove(await GetAsync(name, ct));
        await db.SaveChangesAsync(ct);
    }

    private async Task<Admin> GetAsync(string name, CancellationToken ct)
    {
        var key = AdminName.Key(name.Trim());
        return await db.Admins.SingleOrDefaultAsync(admin => admin.NameKey == key, ct)
            ?? throw new DomainException($"администратор «{name.Trim()}» не найден");
    }

    private string Renew(Admin admin)
    {
        var password = RandomNumberGenerator.GetString(Alphabet, PasswordLength);
        admin.PasswordHash = hasher.HashPassword(admin, password);
        admin.SecurityStamp = Guid.NewGuid().ToString("N");
        return password;
    }
}
