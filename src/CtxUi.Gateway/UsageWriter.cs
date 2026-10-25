using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Usage;

namespace CtxUi.Gateway;

public sealed partial class UsageWriter(
    ClientTouch touch,
    DeviceTouch deviceTouch,
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
            await touch.FlushAsync();
            await deviceTouch.FlushAsync();
        }
        catch (Exception exception)
        {
            TouchFailed(logger, exception);
        }

        try
        {
            await journal.FlushAsync();
        }
        catch (Exception exception)
        {
            JournalFailed(logger, exception);
        }

        try
        {
            await PruneAsync();
        }
        catch (Exception exception)
        {
            PruneFailed(logger, exception);
        }
    }

    private async Task PruneAsync()
    {
        var now = clock.GetUtcNow();
        if (_prunedAt is { } prunedAt && now - prunedAt < PruneEvery)
            return;

        _prunedAt = now;
        await using var scope = scopes.CreateAsyncScope();
        var removed = await scope.ServiceProvider.GetRequiredService<UsageStore>()
            .PruneAsync(now.AddDays(-options.Value.UsageRetentionDays));
        if (removed > 0)
            Pruned(logger, removed);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "failed to save when clients and devices were last seen")]
    private static partial void TouchFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "failed to write the usage journal")]
    private static partial void JournalFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "failed to prune old usage records")]
    private static partial void PruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "old usage records pruned: {Removed}")]
    private static partial void Pruned(ILogger logger, int removed);
}
