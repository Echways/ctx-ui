using CtxUi.Builder.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace CtxUi.Builder.Tests;

public sealed class MigrationTests : IDisposable
{
    private const string BeforeBaseUrl = "20261101210702_SettingsRevision";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public MigrationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    [Fact]
    public async Task Existing_releases_get_the_base_url_of_their_servers_draft()
    {
        var options = new DbContextOptionsBuilder<BuilderDatabase>().UseSqlite($"Data Source={Path.Combine(_directory, "data.db")}").Options;
        await using var database = new BuilderDatabase(options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeBaseUrl);
        var withUrl = Guid.NewGuid().ToString().ToUpperInvariant();
        var withoutUrl = Guid.NewGuid().ToString().ToUpperInvariant();
        foreach (var (id, slug, baseUrl) in new[] { (withUrl, "with-url", "https://api.example.com"), (withoutUrl, "without-url", null) })
        {
            var release = Guid.NewGuid().ToString().ToUpperInvariant();
            await database.Database.ExecuteSqlAsync($"INSERT INTO Servers (Id, Slug, Name, NetworkAccess, MemoryMb, CpuLimit, PidsLimit, RouteSecretSealed, CreatedAt) VALUES ({id}, {slug}, {slug}, 'InternetOnly', 512, 1, 256, 'sealed', 0)");
            await database.Database.ExecuteSqlAsync($"INSERT INTO Drafts (ServerId, BaseUrl, UpdatedAt, Revision) VALUES ({id}, {baseUrl}, 0, 0)");
            await database.Database.ExecuteSqlAsync($"INSERT INTO Releases (Id, ServerId, Number, Status, CreatedAt) VALUES ({release}, {id}, 1, 'Active', 0)");
        }

        await migrator.MigrateAsync();

        var releases = await database.Releases.AsNoTracking().ToListAsync();
        Assert.Equal("https://api.example.com", releases.Single(release => release.ServerId == Guid.Parse(withUrl)).BaseUrl);
        Assert.Null(releases.Single(release => release.ServerId == Guid.Parse(withoutUrl)).BaseUrl);
    }
}
