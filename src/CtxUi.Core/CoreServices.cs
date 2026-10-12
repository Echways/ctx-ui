using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using CtxUi.Core.Clients;
using CtxUi.Core.Data;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Routing;

namespace CtxUi.Core;

public static class CoreServices
{
    public const string DatabaseFile = "ctx-ui.db";

    /// <summary>Регистрирует базу и сервисы. Реализацию ISecretVault и, при желании, TimeProvider даёт вызывающий.</summary>
    public static IServiceCollection AddCtxUiCore(this IServiceCollection services, string dataDir)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, DatabaseFile) }.ToString();
        services.AddDbContext<CtxUiDbContext>(options => options.UseSqlite(connection));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<InboundService>();
        services.AddScoped<ClientService>();
        services.AddScoped<RoutingSnapshotLoader>();
        return services;
    }
}
