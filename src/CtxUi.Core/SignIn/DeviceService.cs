using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;

namespace CtxUi.Core.SignIn;

public sealed record DeviceSeen(string Name, string Ip, string SignedInWith);

public sealed record DeviceAdmission(Device? Device, IReadOnlyList<Device> Active);

public sealed class DeviceService(CtxUiDbContext db, TimeProvider clock)
{
    public async Task<DeviceAdmission> AdmitAsync(Guid clientId, string marker, DeviceSeen seen, CancellationToken ct = default)
    {
        var markerHash = DeviceMarker.Hash(marker);
        var active = await ActiveOf(clientId, await IdleCutoffAsync(ct)).Include(device => device.Apps).ToListAsync(ct);
        if (active.FirstOrDefault(device => device.MarkerHash == markerHash) is { } known)
        {
            known.Name = seen.Name;
            known.SignInIp = seen.Ip;
            known.SignedInWith = seen.SignedInWith;
            await db.SaveChangesAsync(ct);
            return new DeviceAdmission(known, active);
        }

        var limit = await db.Clients.Where(client => client.Id == clientId).Select(client => client.MaxDevices).SingleAsync(ct);
        if (active.Count >= limit)
            return new DeviceAdmission(null, active.OrderByDescending(device => device.CreatedAt).ToList());

        return new DeviceAdmission(await CreateAsync(clientId, markerHash, seen, ct), active);
    }

    public async Task<Device> ReplaceAsync(Guid clientId, Guid replacedId, string marker, DeviceSeen seen, CancellationToken ct = default)
    {
        var replaced = await ActiveOf(clientId, await IdleCutoffAsync(ct)).SingleOrDefaultAsync(device => device.Id == replacedId, ct)
            ?? throw new NotFoundException(CoreText.DeviceNotFound);
        Revoke(replaced, DeviceRevocation.User);
        return await CreateAsync(clientId, DeviceMarker.Hash(marker), seen, ct);
    }

    public async Task<bool> IsActiveAsync(Guid deviceId, CancellationToken ct = default)
    {
        var cutoff = await IdleCutoffAsync(ct);
        return await db.Devices.AsNoTracking().Where(device => device.Id == deviceId).AnyAsync(IsActive(cutoff), ct);
    }

    public async Task<Device> GetAsync(Guid deviceId, CancellationToken ct = default) =>
        await db.Devices.AsNoTracking().Include(device => device.Apps).SingleOrDefaultAsync(device => device.Id == deviceId, ct)
        ?? throw new NotFoundException(CoreText.DeviceNotFound);

    public Task<List<Device>> ListAsync(Guid clientId, DateTimeOffset revokedSince, CancellationToken ct = default) =>
        db.Devices.AsNoTracking()
            .Include(device => device.Apps)
            .Where(device => device.ClientId == clientId && (device.RevokedAt == null || device.RevokedAt >= revokedSince))
            .OrderByDescending(device => device.CreatedAt).ThenBy(device => device.Id)
            .ToListAsync(ct);

    public async Task<int> ActiveCountAsync(Guid clientId, CancellationToken ct = default) =>
        await ActiveOf(clientId, await IdleCutoffAsync(ct)).CountAsync(ct);

    public async Task<Dictionary<Guid, int>> ActiveCountsAsync(CancellationToken ct = default)
    {
        var cutoff = await IdleCutoffAsync(ct);
        return await db.Devices.AsNoTracking()
            .Where(IsActive(cutoff))
            .GroupBy(device => device.ClientId)
            .Select(group => new { ClientId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.ClientId, row => row.Count, ct);
    }

    public Task<string?> AppAuthorizationAsync(Guid deviceId, string appKey, CancellationToken ct = default) =>
        db.DeviceApps.AsNoTracking()
            .Where(app => app.DeviceId == deviceId && app.AppKey == appKey)
            .Select(app => app.AuthorizationId)
            .SingleOrDefaultAsync(ct);

    public async Task AddAppAsync(Guid deviceId, string appKey, string appName, string authorizationId, CancellationToken ct = default)
    {
        var app = await db.DeviceApps.SingleOrDefaultAsync(saved => saved.DeviceId == deviceId && saved.AppKey == appKey, ct);
        if (app is null)
        {
            app = new DeviceApp { DeviceId = deviceId, AppKey = appKey, AppName = appName, AuthorizationId = authorizationId };
            db.DeviceApps.Add(app);
        }

        app.AppName = appName;
        app.AuthorizationId = authorizationId;
        app.ConnectedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task RevokeAsync(Guid clientId, Guid deviceId, DeviceRevocation reason, CancellationToken ct = default)
    {
        var device = await db.Devices.SingleOrDefaultAsync(saved => saved.Id == deviceId && saved.ClientId == clientId, ct)
            ?? throw new NotFoundException(CoreText.DeviceNotFound);
        if (device.RevokedAt is not null)
            return;

        Revoke(device, reason);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RevokeAllAsync(Guid clientId, DeviceRevocation reason, CancellationToken ct = default)
    {
        var devices = await db.Devices.Where(device => device.ClientId == clientId && device.RevokedAt == null).ToListAsync(ct);
        foreach (var device in devices)
            Revoke(device, reason);

        await db.SaveChangesAsync(ct);
        return devices.Count;
    }

    public async Task<int> RevokeIdleAsync(CancellationToken ct = default)
    {
        var cutoff = await IdleCutoffAsync(ct);
        var idle = await db.Devices.Where(device => device.RevokedAt == null && (device.LastUsedAt ?? device.CreatedAt) < cutoff).ToListAsync(ct);
        foreach (var device in idle)
            Revoke(device, DeviceRevocation.Idle);

        await db.SaveChangesAsync(ct);
        return idle.Count;
    }

    public Task<int> PruneAsync(DateTimeOffset revokedBefore, CancellationToken ct = default) =>
        db.Devices.Where(device => device.RevokedAt < revokedBefore).ExecuteDeleteAsync(ct);

    public async Task MarkUsedAsync(IReadOnlyCollection<KeyValuePair<Guid, DateTimeOffset>> seen, CancellationToken ct = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var (deviceId, at) in seen)
        {
            await db.Devices
                .Where(device => device.Id == deviceId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(device => device.LastUsedAt, at), ct);
        }

        await transaction.CommitAsync(ct);
    }

    public Task<Dictionary<Guid, string>> NamesAsync(CancellationToken ct = default) =>
        db.Devices.AsNoTracking()
            .Select(device => new { device.Id, device.Name })
            .ToDictionaryAsync(row => row.Id, row => row.Name, ct);

    private static Expression<Func<Device, bool>> IsActive(DateTimeOffset cutoff) =>
        device => device.RevokedAt == null && (device.LastUsedAt ?? device.CreatedAt) >= cutoff;

    private IQueryable<Device> ActiveOf(Guid clientId, DateTimeOffset cutoff) =>
        db.Devices.Where(device => device.ClientId == clientId).Where(IsActive(cutoff));

    private async Task<DateTimeOffset> IdleCutoffAsync(CancellationToken ct)
    {
        var days = await db.SignInSettings.AsNoTracking().Select(settings => settings.IdleDays).SingleAsync(ct);
        return clock.GetUtcNow().AddDays(-days);
    }

    private async Task<Device> CreateAsync(Guid clientId, string markerHash, DeviceSeen seen, CancellationToken ct)
    {
        var device = new Device
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            MarkerHash = markerHash,
            Name = seen.Name,
            SignInIp = seen.Ip,
            SignedInWith = seen.SignedInWith,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(ct);
        return device;
    }

    private void Revoke(Device device, DeviceRevocation reason)
    {
        device.RevokedAt = clock.GetUtcNow();
        device.RevokedReason = reason;
    }
}
