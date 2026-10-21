using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CtxUi.Core.Clients;

namespace CtxUi.Gateway;

public sealed partial class QuotaSaver(
    QuotaLedger ledger,
    IServiceScopeFactory scopes,
    IOptions<GatewayOptions> options,
    ILogger<QuotaSaver> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SaveAsync();
        }
        catch (OperationCanceledException)
        {
        }

        await SaveAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await ledger.FlushAsync(async usage =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ClientService>().SaveUsageAsync(usage);
            });
        }
        catch (Exception exception)
        {
            SaveFailed(logger, exception, options.Value.FlushInterval);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "failed to save quota counters, next attempt in {Interval}")]
    private static partial void SaveFailed(ILogger logger, Exception exception, TimeSpan interval);
}
