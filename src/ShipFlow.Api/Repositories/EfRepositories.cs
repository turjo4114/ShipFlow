using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShipFlow.Api.Data;
using ShipFlow.Api.Domain;
using ShipFlow.Api.Infrastructure;

namespace ShipFlow.Api.Repositories;

public sealed class ProductRepository(ShipFlowDbContext db) : IProductRepository
{
    public Task<Product?> GetByIdAsync(int id, CancellationToken ct) =>
        db.Products.FirstOrDefaultAsync(p => p.ProductId == id, ct);

    public async Task<IReadOnlyList<Product>> ListAsync(int skip, int take, CancellationToken ct) =>
        await db.Products.AsNoTracking().OrderBy(p => p.Sku).Skip(skip).Take(take).ToListAsync(ct);

    public void Add(Product product) => db.Products.Add(product);
}

public sealed class WarehouseRepository(ShipFlowDbContext db) : IWarehouseRepository
{
    public Task<Warehouse?> GetByIdAsync(int id, CancellationToken ct) =>
        db.Warehouses.FirstOrDefaultAsync(w => w.WarehouseId == id, ct);

    public async Task<IReadOnlyList<Warehouse>> ListAsync(CancellationToken ct) =>
        await db.Warehouses.AsNoTracking().OrderBy(w => w.Code).ToListAsync(ct);

    public void Add(Warehouse warehouse) => db.Warehouses.Add(warehouse);
}

public sealed class StockMovementRepository(ShipFlowDbContext db) : IStockMovementRepository
{
    public void Add(StockMovement movement) => db.StockMovements.Add(movement);

    public async Task<IReadOnlyList<StockMovement>> ListAsync(int productId, int? warehouseId, int take, CancellationToken ct)
    {
        var q = db.StockMovements.AsNoTracking().Where(m => m.ProductId == productId);
        if (warehouseId.HasValue) q = q.Where(m => m.WarehouseId == warehouseId.Value);
        return await q.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.MovementId).Take(take).ToListAsync(ct);
    }

    public Task<int> CountByIdsAsync(long[] ids, CancellationToken ct) =>
        db.StockMovements.CountAsync(m => ids.Contains(m.MovementId), ct);
}

public sealed class OutboxWriter(ShipFlowDbContext db, ITenantContext tenant) : IOutboxWriter
{
    public void Enqueue(string messageType, object payload) =>
        db.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenant.TenantId,
            MessageType = messageType,
            Payload = JsonSerializer.Serialize(payload),
        });
}

public sealed class EfUnitOfWork(ShipFlowDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
