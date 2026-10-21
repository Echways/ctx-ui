using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;
using CtxUi.Core.Tests;

namespace CtxUi.Gateway.Tests;

public sealed class ClientTouchTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FlakyUpdates _updates = new();
    private readonly ServiceProvider _services;
    private readonly ClientTouch _touch;

    public ClientTouchTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<CtxUiDbContext>(options => options.UseSqlite(_connection).AddInterceptors(_updates))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISecretVault, FakeVault>()
            .AddScoped<InboundService>()
            .AddScoped<ClientService>()
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
            scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.Migrate();

        _touch = new ClientTouch(_services.GetRequiredService<IServiceScopeFactory>());
    }

    private async Task<T> UseAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private async Task<Guid> AddClientAsync()
    {
        await UseAsync(services => services.GetRequiredService<InboundService>().AddHttpAsync("echo", null, "https://mcp.example.org/mcp"));
        var issued = await UseAsync(services => services.GetRequiredService<ClientService>().AddAsync("Иванов", ["echo"]));
        return issued.Client.Id;
    }

    private Task<Client> ReadClientAsync() =>
        UseAsync(services => services.GetRequiredService<ClientService>().GetAsync("Иванов"));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Use_is_written_only_by_the_flush()
    {
        var clientId = await AddClientAsync();

        _touch.Touch(clientId, Now);
        var before = await ReadClientAsync();
        await _touch.FlushAsync();
        var after = await ReadClientAsync();

        Assert.Null(before.LastUsedAt);
        Assert.Equal(Now, after.LastUsedAt);
    }

    [Fact]
    public async Task Use_that_failed_to_save_is_saved_by_the_next_flush()
    {
        var clientId = await AddClientAsync();
        _touch.Touch(clientId, Now);

        _updates.FailuresLeft = 1;
        await Assert.ThrowsAsync<DbUpdateException>(() => _touch.FlushAsync());
        await _touch.FlushAsync();

        Assert.Equal(Now, (await ReadClientAsync()).LastUsedAt);
    }

    [Fact]
    public async Task Later_use_arriving_during_a_flush_is_not_lost()
    {
        var clientId = await AddClientAsync();
        _touch.Touch(clientId, Now);

        _updates.FailuresLeft = 1;
        await Assert.ThrowsAsync<DbUpdateException>(() => _touch.FlushAsync());
        _touch.Touch(clientId, Now.AddMinutes(2));
        await _touch.FlushAsync();

        Assert.Equal(Now.AddMinutes(2), (await ReadClientAsync()).LastUsedAt);
    }

    private sealed class FlakyUpdates : DbCommandInterceptor
    {
        public int FailuresLeft { get; set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailuresLeft > 0 && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
            {
                FailuresLeft--;
                throw new DbUpdateException("write failed");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
