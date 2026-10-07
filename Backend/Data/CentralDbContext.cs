using Backend.Domain;
using Microsoft.EntityFrameworkCore;

namespace Backend.Data;

public sealed class CentralDbContext(DbContextOptions<CentralDbContext> options) : DbContext(options)
{
    public DbSet<CentralSyncEvent> SyncEvents => Set<CentralSyncEvent>();
    public DbSet<CentralProduct> Products => Set<CentralProduct>();
    public DbSet<CentralSale> Sales => Set<CentralSale>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CentralSyncEvent>()
            .HasIndex(item => new { item.ClientId, item.LocalEventId })
            .IsUnique();
        modelBuilder.Entity<CentralSyncEvent>()
            .HasIndex(item => new { item.EntityType, item.EntityId, item.ReceivedAtUtc });
        modelBuilder.Entity<CentralProduct>()
            .HasIndex(item => new { item.ClientId, item.Barcode })
            .IsUnique();
        modelBuilder.Entity<CentralProduct>().Property(item => item.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<CentralProduct>().Property(item => item.TaxRate).HasPrecision(6, 4);
        modelBuilder.Entity<CentralProduct>().Property(item => item.Stock).HasPrecision(18, 3);
        modelBuilder.Entity<CentralProduct>().Property(item => item.MinimumStock).HasPrecision(18, 3);
        modelBuilder.Entity<CentralSale>()
            .HasIndex(item => new { item.ClientId, item.LocalSaleId })
            .IsUnique();
        modelBuilder.Entity<CentralSale>().Property(item => item.Subtotal).HasPrecision(18, 2);
        modelBuilder.Entity<CentralSale>().Property(item => item.DiscountTotal).HasPrecision(18, 2);
        modelBuilder.Entity<CentralSale>().Property(item => item.TaxTotal).HasPrecision(18, 2);
        modelBuilder.Entity<CentralSale>().Property(item => item.Total).HasPrecision(18, 2);
    }
}

public sealed record SyncBatchRequest(string ClientId, List<SyncEventRequest> Events);

public sealed record SyncEventRequest(
    long LocalEventId,
    DateTime CreatedAtUtc,
    string EntityType,
    string EntityId,
    string Operation,
    string Payload);
