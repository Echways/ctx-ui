using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CtxUi.Builder.Data;

public sealed class BuilderDatabase(DbContextOptions<BuilderDatabase> options) : DbContext(options)
{
    public DbSet<BuilderServer> Servers => Set<BuilderServer>();

    public DbSet<ServerDraft> Drafts => Set<ServerDraft>();

    public DbSet<DraftFile> Files => Set<DraftFile>();

    public DbSet<ServerSecret> Secrets => Set<ServerSecret>();

    public DbSet<Release> Releases => Set<Release>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<BuilderServer>(server =>
        {
            server.HasKey(entity => entity.Id);
            server.HasIndex(entity => entity.Slug).IsUnique();
            server.Property(entity => entity.Slug).HasMaxLength(32);
            server.Property(entity => entity.Name).HasMaxLength(120);
            server.Property(entity => entity.NetworkAccess).HasConversion<string>().HasMaxLength(16);
            server.Property(entity => entity.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
        });

        model.Entity<ServerDraft>(draft =>
        {
            draft.HasKey(entity => entity.ServerId);
            draft.Property(entity => entity.UpdatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            draft.HasOne<BuilderServer>().WithOne().HasForeignKey<ServerDraft>(entity => entity.ServerId).OnDelete(DeleteBehavior.Cascade);
            draft.HasMany(entity => entity.Files).WithOne().HasForeignKey(file => file.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<DraftFile>(file =>
        {
            file.HasKey(entity => new { entity.ServerId, entity.Path });
            file.Property(entity => entity.Path).HasMaxLength(512);
            file.Property(entity => entity.Revision).IsConcurrencyToken();
        });

        model.Entity<ServerSecret>(secret =>
        {
            secret.HasKey(entity => new { entity.ServerId, entity.Name });
            secret.Property(entity => entity.Name).HasMaxLength(64);
            secret.HasOne<BuilderServer>().WithMany().HasForeignKey(entity => entity.ServerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Release>(release =>
        {
            release.HasKey(entity => entity.Id);
            release.HasIndex(entity => new { entity.ServerId, entity.Number }).IsUnique();
            release.Property(entity => entity.Status).HasConversion<string>().HasMaxLength(16);
            release.Property(entity => entity.CreatedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            release.Property(entity => entity.FinishedAt).HasConversion(new DateTimeOffsetToBinaryConverter());
            release.HasOne<BuilderServer>().WithMany().HasForeignKey(entity => entity.ServerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
