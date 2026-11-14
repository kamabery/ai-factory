using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>
/// The board and events as tables (contract, "State backends", Database), TenantId in every key but Events'. One
/// subclass per provider, each with its own migrations.
/// </summary>
public abstract class FactoryDb(DbContextOptions options) : DbContext(options)
{
    public DbSet<BoardRow> Boards => Set<BoardRow>();
    public DbSet<IdeaRow> Ideas => Set<IdeaRow>();
    public DbSet<JobRow> Jobs => Set<JobRow>();
    public DbSet<EventRow> Events => Set<EventRow>();
    public DbSet<LeaseRow> Leases => Set<LeaseRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<BoardRow>(e =>
        {
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<IdeaRow>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.Target).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
        b.Entity<JobRow>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Id });
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.Id).HasMaxLength(32);
            e.Property(x => x.Idea).HasMaxLength(32);
            e.Property(x => x.Stage).HasMaxLength(32);
            e.Property(x => x.Target).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.TenantId, x.Stage });
        });
        b.Entity<EventRow>(e =>
        {
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.Ts).HasMaxLength(40);
            e.Property(x => x.Job).HasMaxLength(32);
            e.Property(x => x.Idea).HasMaxLength(32);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.HasIndex(x => new { x.TenantId, x.Seq });
            e.HasIndex(x => new { x.TenantId, x.Job });
        });
        b.Entity<LeaseRow>(e =>
        {
            e.HasKey(x => new { x.TenantId, x.Name });
            e.Property(x => x.TenantId).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(64);
            e.Property(x => x.Holder).HasMaxLength(64);
        });
    }
}
