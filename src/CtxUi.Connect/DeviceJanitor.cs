using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using CtxUi.Connect.Registration;
using CtxUi.Core.Data;
using CtxUi.Core.Routing;
using CtxUi.Core.SignIn;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace CtxUi.Connect;

public sealed partial class DeviceJanitor(
    IServiceScopeFactory scopes,
    ISnapshotSource snapshot,
    TimeProvider clock,
    ILogger<DeviceJanitor> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RevokedRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan UnusedAppRetention = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    SweepFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var devices = services.GetRequiredService<DeviceService>();
        var authorizations = services.GetRequiredService<IOpenIddictAuthorizationManager>();
        var db = services.GetRequiredService<CtxUiDbContext>();
        var now = clock.GetUtcNow();

        var idle = await devices.RevokeIdleAsync(ct);
        var ofRevokedDevices = await db.DeviceApps.AsNoTracking()
            .Where(app => db.Devices.Any(device => device.Id == app.DeviceId && device.RevokedAt != null))
            .Select(app => app.AuthorizationId)
            .ToHashSetAsync(ct);
        var valid = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            .Where(authorization => authorization.Id != null && authorization.Subject != null && authorization.Status == Statuses.Valid)
            .Select(authorization => new { Id = authorization.Id!, Subject = authorization.Subject! })
            .ToListAsync(ct);
        var liveClients = await db.Clients.AsNoTracking().Select(client => client.Id.ToString()).ToHashSetAsync(ct);
        foreach (var authorization in valid.Where(authorization => ofRevokedDevices.Contains(authorization.Id) || !liveClients.Contains(authorization.Subject)))
            await RevokeAsync(authorizations, authorization.Id, ct);

        var pruned = await devices.PruneAsync(now - RevokedRetention, ct);
        var tokenThreshold = now.AddDays(-snapshot.Current.SignIn.IdleDays) - RevokedRetention;
        await services.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(tokenThreshold, ct);
        await authorizations.PruneAsync(tokenThreshold - RevokedRetention, ct);

        await using var appsScope = scopes.CreateAsyncScope();
        await appsScope.ServiceProvider.GetRequiredService<RegisteredApps>().PruneAsync(now - UnusedAppRetention, ct);

        if (idle > 0 || pruned > 0)
            await snapshot.RefreshAsync();
    }

    private static async Task RevokeAsync(IOpenIddictAuthorizationManager authorizations, string id, CancellationToken ct)
    {
        if (await authorizations.FindByIdAsync(id, ct) is { } authorization)
            await authorizations.TryRevokeAsync(authorization, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "sweeping devices and tokens failed, will retry on the next pass")]
    private static partial void SweepFailed(ILogger logger, Exception exception);
}
