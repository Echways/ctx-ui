using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CtxUi.Core.Clients;

namespace CtxUi.Gateway;

public sealed class ClientTouch(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ClientTouch> logger)
{
    private readonly TouchThrottle _throttle = new(TimeSpan.FromMinutes(1));

    /// <summary>Сбой записи статистики не должен ронять запрос клиента, поэтому метод не бросает.</summary>
    public async Task TouchAsync(Guid clientId)
    {
        var now = clock.GetUtcNow();
        if (!_throttle.ShouldTouch(clientId, now))
            return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ClientService>().MarkUsedAsync(clientId, now);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "не удалось записать время обращения клиента {ClientId}", clientId);
        }
    }
}
