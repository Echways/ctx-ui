using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CtxUi.Gateway;

public sealed partial class SnapshotRefresher(SnapshotStore store, IOptions<GatewayOptions> options, ILogger<SnapshotRefresher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await store.ReloadIfChangedAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                RefreshFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "failed to refresh the routing table, the previous one stays in use")]
    private static partial void RefreshFailed(ILogger logger, Exception exception);
}
