using Microsoft.EntityFrameworkCore;
using ShipFlow.Api.Domain;
using ShipFlow.Api.Infrastructure;

namespace ShipFlow.Api.Data;

/// <summary>
/// DB-first: the schema is owned by /db/init/*.sql, EF is only the mapper + unit of work.
/// Tenant isolation = global query filters (reads) + TenantId stamping/guard on SaveChanges (writes).
/// </summary>
public sealed class ShipFlowDbContext : DbContext
{
    private readonly ITenantContext _tenant;   // explicit field: EF parameterises filters per context instance

    public ShipFlowDbContext(DbContextOptions<ShipFlowDbContext> options, ITenantContext tenant) : base(options)
    {
        _tenant = tenant;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Tenant>(e => { e.ToTable("Tenants"); e.HasKey(x => x.TenantId); });

        mb.Entity<Warehouse>(e =>
        {
            e.ToTable("Warehouses"); e.HasKey(x => x.WarehouseId);
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        mb.Entity<Product>(e =>
        {
            e.ToTable("Products"); e.HasKey(x => x.ProductId);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        mb.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovements"); e.HasKey(x => x.MovementId);
            e.Property(x => x.QuantityDelta).HasPrecision(18, 3);
            e.Property(x => x.OccurredAt).HasColumnType("datetime2(3)");
            e.HasQueryFilter(x => x.TenantId == _tenant.TenantId);
        });

        // No query filter: the outbox is drained by a background service that has no tenant context.
        mb.Entity<OutboxMessage>(e => { e.ToTable("OutboxMessages"); e.HasKey(x => x.OutboxMessageId); });
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>().Where(e => e.State == EntityState.Added))
        {
            if (entry.Entity.TenantId == 0) entry.Entity.TenantId = _tenant.TenantId;
            else if (entry.Entity.TenantId != _tenant.TenantId)
                throw new InvalidOperationException("Cross-tenant write blocked.");
        }
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }
}
