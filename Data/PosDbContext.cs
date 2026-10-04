using Microsoft.EntityFrameworkCore;
using SupermercadoPOS.Domain;

namespace SupermercadoPOS.Data;

public sealed class PosDbContext(DbContextOptions<PosDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PosUser> Users => Set<PosUser>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleItem> SaleItems => Set<SaleItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<CashShift> CashShifts => Set<CashShift>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SyncQueueItem> SyncQueue => Set<SyncQueueItem>();

    public static PosDbContext Create(string databasePath)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new PosDbContext(options);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>().HasIndex(p => p.Barcode).IsUnique();
        modelBuilder.Entity<PosUser>().HasIndex(u => u.Username).IsUnique();
        modelBuilder.Entity<Sale>().HasMany(s => s.Items).WithOne(i => i.Sale).HasForeignKey(i => i.SaleId);
        modelBuilder.Entity<Sale>().HasMany(s => s.Payments).WithOne(p => p.Sale).HasForeignKey(p => p.SaleId);
        modelBuilder.Entity<CashShift>().HasIndex(s => new { s.CashierId, s.Status });
        modelBuilder.Entity<SyncQueueItem>().HasIndex(q => new { q.Status, q.CreatedAtUtc });

        modelBuilder.Entity<Product>().Property(p => p.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.TaxRate).HasPrecision(6, 4);
        modelBuilder.Entity<Product>().Property(p => p.Stock).HasPrecision(18, 3);
        modelBuilder.Entity<Product>().Property(p => p.MinimumStock).HasPrecision(18, 3);
        modelBuilder.Entity<Sale>().Property(s => s.Total).HasPrecision(18, 2);
        modelBuilder.Entity<Sale>().Property(s => s.Subtotal).HasPrecision(18, 2);
        modelBuilder.Entity<Sale>().Property(s => s.TaxTotal).HasPrecision(18, 2);
        modelBuilder.Entity<SaleItem>().Property(i => i.Quantity).HasPrecision(18, 3);
        modelBuilder.Entity<SaleItem>().Property(i => i.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Payment>().Property(p => p.Amount).HasPrecision(18, 2);
    }
}
