namespace ShipFlow.Api.Contracts;

public record CreateProductRequest(string Sku, string Name, string? UnitOfMeasure);
public record CreateWarehouseRequest(string Code, string Name);
public record CreateMovementRequest(
    int WarehouseId, int ProductId, string MovementType, decimal Quantity,
    string? ReferenceNo, string? Notes, DateTime? OccurredAt);
public record BulkLogisticsRequest(long[] MovementIds);
