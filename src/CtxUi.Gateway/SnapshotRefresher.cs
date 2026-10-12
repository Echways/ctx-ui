using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CtxUi.Gateway;

/// <summary>Подхватывает изменения, сделанные из другого процесса, например из CLI.</summary>
public sealed class SnapshotRefresher(SnapshotStore store, IOptions<GatewayOptions> options, ILogger<SnapshotRefresher> logger)
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
                logger.LogError(exception, "не удалось обновить таблицу маршрутов, работает прежняя");
            }
        }
    }
}
