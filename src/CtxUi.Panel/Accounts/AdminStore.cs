using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Admins;
using CtxUi.Core.Data;

namespace CtxUi.Panel.Accounts;

public sealed class AdminStore(CtxUiDbContext db)
    : IUserPasswordStore<Admin>, IUserSecurityStampStore<Admin>, IUserLockoutStore<Admin>
{
    public async Task<IdentityResult> CreateAsync(Admin user, CancellationToken cancellationToken)
    {
        db.Admins.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(Admin user, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(Admin user, CancellationToken cancellationToken)
    {
        db.Admins.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        return IdentityResult.Success;
    }

    public async Task<Admin?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(userId, out var id)
            ? await db.Admins.SingleOrDefaultAsync(admin => admin.Id == id, cancellationToken)
            : null;

    public Task<Admin?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) =>
        db.Admins.SingleOrDefaultAsync(admin => admin.NameKey == normalizedUserName, cancellationToken);

    public Task<string> GetUserIdAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult(user.Id.ToString());

    public Task<string?> GetUserNameAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.Name);

    public Task SetUserNameAsync(Admin user, string? userName, CancellationToken cancellationToken)
    {
        user.Name = userName ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.NameKey);

    public Task SetNormalizedUserNameAsync(Admin user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NameKey = normalizedName ?? "";
        return Task.CompletedTask;
    }

    public Task SetPasswordHashAsync(Admin user, string? passwordHash, CancellationToken cancellationToken)
    {
        user.PasswordHash = passwordHash ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.PasswordHash.Length == 0 ? null : user.PasswordHash);

    public Task<bool> HasPasswordAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult(user.PasswordHash.Length > 0);

    public Task SetSecurityStampAsync(Admin user, string stamp, CancellationToken cancellationToken)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(user.SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult(user.LockedUntil);

    public Task SetLockoutEndDateAsync(Admin user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken)
    {
        user.LockedUntil = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult(++user.FailedAttempts);

    public Task ResetAccessFailedCountAsync(Admin user, CancellationToken cancellationToken)
    {
        user.FailedAttempts = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(Admin user, CancellationToken cancellationToken) =>
        Task.FromResult(user.FailedAttempts);

    public Task<bool> GetLockoutEnabledAsync(Admin user, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task SetLockoutEnabledAsync(Admin user, bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
