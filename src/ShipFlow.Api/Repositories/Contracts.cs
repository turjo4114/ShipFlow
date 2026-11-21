using ShipFlow.Api.Domain;

namespace ShipFlow.Api.Repositories;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<Product>> ListAsync(int skip, int take, CancellationToken ct);
    void Add(Product product);
}

public interface IWarehouseRepository
{
    Task<Warehouse?> GetByIdAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<Warehouse>> ListAsync(CancellationToken ct);
    void Add(Warehouse warehouse);
}

public interface IStockMovementRepository
{
    void Add(StockMovement movement);
    Task<IReadOnlyList<StockMovement>> ListAsync(int productId, int? warehouseId, int take, CancellationToken ct);
    Task<int> CountByIdsAsync(long[] ids, CancellationToken ct);
}

/// <summary>Stages an outbox row in the SAME unit of work as the business change (atomic with it).</summary>
public interface IOutboxWriter
{
    void Enqueue(string messageType, object payload);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);
}

// ---- Read side (Dapper + inline TVFs) ----
public sealed class StockSheetRow
{
    public long MovementId { get; set; }
    public DateTime OccurredAt { get; set; }
    public string MovementType { get; set; } = "";
    public decimal QuantityDelta { get; set; }
    public decimal RunningBalance { get; set; }
    public string? ReferenceNo { get; set; }
}

public sealed class StockOnHandRow
{
    public int WarehouseId { get; set; }
    public string WarehouseCode { get; set; } = "";
    public int ProductId { get; set; }
    public string Sku { get; set; } = "";
    public decimal OnHand { get; set; }
    public decimal TotalOnHand { get; set; }
    public decimal? SharePct { get; set; }
}

public interface IStockReportReader
{
    Task<IReadOnlyList<StockSheetRow>> GetSheetAsync(int tenantId, int warehouseId, int productId, DateTime? from, DateTime? to, CancellationToken ct);
    Task<IReadOnlyList<StockOnHandRow>> GetOnHandAsync(int tenantId, DateTime? asOf, CancellationToken ct);
}
