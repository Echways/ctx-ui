using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using CtxUi.Core.Clients;
using CtxUi.Core.Inbounds;

namespace CtxUi.Core.Data;

public sealed class CtxUiDbContext(DbContextOptions<CtxUiDbContext> options) : DbContext(options)
{
    public DbSet<Inbound> Inbounds => Set<Inbound>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientInbound> ClientInbounds => Set<ClientInbound>();
    public DbSet<RoutingState> RoutingStates => Set<RoutingState>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisConverter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Inbound>(entity => entity.HasIndex(inbound => inbound.Slug).IsUnique());

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

        modelBuilder.Entity<RoutingState>().HasData(new RoutingState { Id = 1, Revision = 0 });
    }

    /// <summary>
    /// Любое отслеживаемое изменение меняет маршруты, поэтому вместе с ним растёт ревизия.
    /// Прирост делается в SQL, чтобы два процесса не затёрли изменения друг друга.
    /// Обновления времени обращения идут через ExecuteUpdateAsync и ревизию не трогают.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (!ChangeTracker.HasChanges())
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
}
