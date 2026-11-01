using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Extensions;

public sealed class ExtensionContext
{
    private const string DatabaseFile = "data.db";

    internal ExtensionContext(string id, PanelEditor panel, IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        Id = id;
        Panel = panel;
        Services = services;
        Configuration = configuration;
        DataDirectory = dataDirectory;
    }

    public string Id { get; }

    public PanelEditor Panel { get; }

    public IServiceCollection Services { get; }

    public IConfiguration Configuration { get; }

    public string DataDirectory { get; }

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
