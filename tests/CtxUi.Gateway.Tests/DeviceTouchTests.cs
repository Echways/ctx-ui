using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Secrets;
using CtxUi.Core.SignIn;
using CtxUi.Core.Tests;

namespace CtxUi.Gateway.Tests;

public sealed class DeviceTouchTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly ServiceProvider _services;
    private readonly DeviceTouch _touch;

    public DeviceTouchTests()
    {
        _connection.Open();
        _services = new ServiceCollection()
            .AddDbContext<CtxUiDbContext>(options => options.UseSqlite(_connection))
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISecretVault, FakeVault>()
            .AddScoped<InboundService>()
            .AddScoped<ClientService>()
            .AddScoped<DeviceService>()
            .BuildServiceProvider();
        using (var scope = _services.CreateScope())
            scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.Migrate();

        _touch = new DeviceTouch(_services.GetRequiredService<IServiceScopeFactory>());
    }

    private async Task<T> UseAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private async Task<Guid> AddDeviceAsync()
    {
        await UseAsync(services => services.GetRequiredService<InboundService>().AddHttpAsync("echo", null, "https://mcp.example.org/mcp"));
        var issued = await UseAsync(services => services.GetRequiredService<ClientService>().AddAsync("Иванов", ["echo"]));
        var admission = await UseAsync(services => services.GetRequiredService<DeviceService>()
            .AdmitAsync(issued.Client.Id, DeviceMarker.Generate(), new DeviceSeen("Chrome · Windows", "203.0.113.7", Device.ByKey)));
        return admission.Device!.Id;
    }

    private Task<DateTimeOffset?> LastUsedAsync(Guid deviceId) =>
        UseAsync(async services => (await services.GetRequiredService<DeviceService>().GetAsync(deviceId)).LastUsedAt);

    [Fact]
    public async Task Touches_are_saved_on_flush_once_a_minute_per_device()
    {
        var deviceId = await AddDeviceAsync();

        _touch.Touch(deviceId, Now);
        _touch.Touch(deviceId, Now.AddSeconds(30));
        Assert.Null(await LastUsedAsync(deviceId));
        await _touch.FlushAsync();
        Assert.Equal(Now, await LastUsedAsync(deviceId));

        _touch.Touch(deviceId, Now.AddMinutes(2));
        await _touch.FlushAsync();
        await _touch.FlushAsync();
        Assert.Equal(Now.AddMinutes(2), await LastUsedAsync(deviceId));
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
