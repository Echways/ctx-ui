using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using CtxUi.Core.Admins;
using CtxUi.Core.ApiKeys;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;
using CtxUi.Core.Settings;
using CtxUi.Core.Usage;

namespace CtxUi.Core.Data;

public sealed class CtxUiDbContext(DbContextOptions<CtxUiDbContext> options) : DbContext(options)
{
    public DbSet<Inbound> Inbounds => Set<Inbound>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientInbound> ClientInbounds => Set<ClientInbound>();
    public DbSet<RoutingState> RoutingStates => Set<RoutingState>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<UsageDaily> UsageDays => Set<UsageDaily>();
    public DbSet<Admin> Admins => Set<Admin>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Inbound>(entity =>
        {
            entity.HasIndex(inbound => inbound.Slug).IsUnique();
            entity.Property(inbound => inbound.MaxBodyBytes).HasDefaultValue(Inbound.DefaultMaxBodyBytes);
        });

        modelBuilder.Entity<Client>(entity =>
        {
            entity.HasIndex(client => client.NameKey).IsUnique();
            entity.HasIndex(client => client.TokenHash).IsUnique();
            entity.Property(client => client.Metadata).HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null) ?? new(),
                new ValueComparer<Dictionary<string, string>>(
                    (left, right) => left!.Count == right!.Count && !left.Except(right).Any(),
                    value => value.Aggregate(0, (hash, pair) => HashCode.Combine(hash, pair.Key, pair.Value)),
                    value => new Dictionary<string, string>(value)));
            entity.HasMany(client => client.Inbounds).WithOne()
                .HasForeignKey(link => link.ClientId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ClientInbound>(entity =>
        {
            entity.HasKey(link => new { link.ClientId, link.InboundId });
            entity.HasOne(link => link.Inbound).WithMany()
                .HasForeignKey(link => link.InboundId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UsageEvent>(entity =>
        {
            entity.HasIndex(usage => usage.At);
            entity.HasIndex(usage => new { usage.ClientId, usage.At });
        });

        modelBuilder.Entity<UsageDaily>(entity =>
            entity.HasKey(daily => new { daily.Day, daily.ClientId, daily.InboundId }));

        modelBuilder.Entity<Admin>(entity => entity.HasIndex(admin => admin.NameKey).IsUnique());

        modelBuilder.Entity<Setting>(entity => entity.HasKey(setting => setting.Key));

        modelBuilder.Entity<ApiKey>(entity =>
        {
            entity.HasIndex(apiKey => apiKey.NameKey).IsUnique();
            entity.HasIndex(apiKey => apiKey.SecretHash).IsUnique();
        });

        modelBuilder.Entity<RoutingState>().HasData(new RoutingState { Id = 1, Revision = 0 });
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!RoutingChanged())
            return await base.SaveChangesAsync(cancellationToken);

        await using var transaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        var written = await base.SaveChangesAsync(cancellationToken);
        await RoutingStates.ExecuteUpdateAsync(
            setters => setters.SetProperty(state => state.Revision, state => state.Revision + 1),
            cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return written;
    }

    private bool RoutingChanged() => ChangeTracker.Entries().Any(entry =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
        entry.Entity is Inbound or Client or ClientInbound);
}
