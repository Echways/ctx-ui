using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Extensions;

public sealed class ExtensionContext(
    string id,
    PanelEditor panel,
    IServiceCollection services,
    IConfiguration configuration,
    string dataDirectory)
{
    private const string DatabaseFile = "data.db";

    public string Id { get; } = id;

    public PanelEditor Panel { get; } = panel;

    public IServiceCollection Services { get; } = services;

    public IConfiguration Configuration { get; } = configuration;

    public string DataDirectory { get; } = dataDirectory;

    public ExtensionContext AddDatabase<TContext>() where TContext : DbContext
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(DataDirectory, DatabaseFile) }.ToString();
        Services.AddDbContext<TContext>(options => options.UseSqlite(connection));
        Services.AddScoped<IExtensionInitializer, DatabaseInitializer<TContext>>();
        return this;
    }

    private sealed class DatabaseInitializer<TContext>(TContext database) : IExtensionInitializer where TContext : DbContext
    {
        public Task InitializeAsync(CancellationToken ct) => database.Database.MigrateAsync(ct);
    }
}
