using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed class UsageWriter(
    UsageJournal journal,
    IServiceScopeFactory scopes,
    IOptions<GatewayOptions> options,
    TimeProvider clock,
    ILogger<UsageWriter> logger) : BackgroundService
{
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(1);

    private DateTimeOffset? _prunedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await WriteAsync();
        }
        catch (OperationCanceledException)
        {
        }

        await WriteAsync();
    }

    private async Task WriteAsync()
    {
        try
        {
            await journal.FlushAsync();

            var now = clock.GetUtcNow();
            if (_prunedAt is { } prunedAt && now - prunedAt < PruneEvery)
                return;

            _prunedAt = now;
            await using var scope = scopes.CreateAsyncScope();
            var removed = await scope.ServiceProvider.GetRequiredService<UsageStore>()
                .PruneAsync(now.AddDays(-options.Value.UsageRetentionDays));
            if (removed > 0)
                logger.LogInformation("из журнала вызовов удалено старых записей: {Removed}", removed);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "не удалось записать журнал вызовов");
        }
    }
}
