using ElecStock.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ElecStock.Data;

public sealed class StockDb(DbContextOptions<StockDb> options) : DbContext(options)
{
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Revision> Revisions => Set<Revision>();
    public DbSet<Allocation> Allocations => Set<Allocation>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<Photo> Photos => Set<Photo>();
    public DbSet<Activity> Activities => Set<Activity>();
    public DbSet<KicadItem> KicadItems => Set<KicadItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configuration)
    {
        // Enums are stored by name: readable in adminer and safe when a value is inserted.
        configuration.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        configuration.Properties<decimal>().HavePrecision(12, 4);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Part>(e =>
        {
            e.HasIndex(p => p.Ref).IsUnique();
            e.Property(p => p.Ref).HasMaxLength(100).UseCollation(CaseInsensitive);
            e.Property(p => p.Category).HasMaxLength(50);
            e.Property(p => p.Qty).HasMaxLength(50);
            e.Property(p => p.KicadSymbol).HasMaxLength(300);
            e.Property(p => p.KicadFootprint).HasMaxLength(300);
            e.Property(p => p.Kicad3dModel).HasMaxLength(500);
            e.HasOne(p => p.Location).WithMany(l => l.Parts).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<Location>(e =>
        {
            e.HasIndex(l => l.Code).IsUnique();
            e.Property(l => l.Code).HasMaxLength(50).UseCollation(CaseInsensitive);
        });

        b.Entity<Project>(e =>
        {
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Name).HasMaxLength(100).UseCollation(CaseInsensitive);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(d => new { d.ProjectId, d.Name }).IsUnique();
            e.Property(d => d.Name).HasMaxLength(100).UseCollation(CaseInsensitive);
            e.HasOne(d => d.Project).WithMany(p => p.Devices).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Revision>(e =>
        {
            e.HasIndex(r => new { r.DeviceId, r.Rev }).IsUnique();
            e.Property(r => r.Rev).HasMaxLength(20).UseCollation(CaseInsensitive);
            e.HasOne(r => r.Device).WithMany(d => d.Revisions).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Allocation>(e =>
        {
            e.HasIndex(a => new { a.RevisionId, a.PartId }).IsUnique();
            e.HasOne(a => a.Revision).WithMany(r => r.Allocations).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Part).WithMany(p => p.Allocations).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<OrderLine>(e =>
        {
            e.HasOne(l => l.Order).WithMany(o => o.Lines).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(l => l.Part).WithMany(p => p.OrderLines).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Photo>(e =>
        {
            e.OwnsMany(p => p.Markers, m => m.ToJson());
            e.HasOne(p => p.Part).WithMany(p => p.Photos).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Project).WithMany(p => p.Photos).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(p => p.Revision).WithMany(r => r.Photos).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<KicadItem>(e =>
        {
            e.HasIndex(i => new { i.Kind, i.Name }).IsUnique();
            e.Property(i => i.Name).HasMaxLength(200).UseCollation(CaseInsensitive);
            e.Property(i => i.Extends).HasMaxLength(200);
        });

        b.Entity<Activity>(e =>
        {
            e.HasIndex(a => a.At);
            e.HasIndex(a => a.PartId);
            e.Property(a => a.Kind).HasMaxLength(40);
        });

        // Case-insensitive, accent-sensitive ICU collation for refs, codes and names.
        b.HasCollation(CaseInsensitive, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
    }

    public const string CaseInsensitive = "ci";
}

/// <summary>Used by <c>dotnet ef</c> to create migrations; the connection is never opened.</summary>
public sealed class StockDbDesignFactory : IDesignTimeDbContextFactory<StockDb>
{
    public StockDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<StockDb>()
            .UseNpgsql("Host=localhost;Port=5433;Database=elecstock;Username=elecstock;Password=password")
            .Options);
}
