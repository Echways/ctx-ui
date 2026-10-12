using Microsoft.EntityFrameworkCore;
using CtxUi.Core.Data;
using CtxUi.Gateway;

namespace CtxUi.Server;

public static class ServerApp
{
    /// <summary>Собирает веб-приложение. Обратный вызов нужен тестам: порт, каталог данных, часы.</summary>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Logging.AddCtxUiFilters();
        builder.Services.AddCtxUi(builder.Configuration);
        builder.Services.AddCtxUiGateway(builder.Configuration.GetSection(CtxUiOptions.Section));

        var app = builder.Build();
        app.UseCtxUiGateway();
        return app;
    }

    /// <summary>Применяет миграции и загружает маршруты. Вызывается до старта, чтобы первый запрос не увидел пустую таблицу.</summary>
    public static async Task InitializeAsync(WebApplication app, CancellationToken ct = default)
    {
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<CtxUiDbContext>().Database.MigrateAsync(ct);

        await app.Services.GetRequiredService<SnapshotStore>().ReloadAsync(ct);
    }
}
