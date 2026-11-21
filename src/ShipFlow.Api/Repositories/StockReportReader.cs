using Dapper;
using ShipFlow.Api.Infrastructure;

namespace ShipFlow.Api.Repositories;

/// <summary>
/// Analytics reads: hand-written T-SQL over inline table-valued functions (window functions).
/// Dapper bypasses EF's global query filters, so tenantId is a REQUIRED parameter on every method (ADR-002).
/// </summary>
public sealed class StockReportReader(ISqlConnectionFactory factory) : IStockReportReader
{
    private static readonly DateTime MinDate = new(1900, 1, 1);
    private static readonly DateTime MaxDate = new(9999, 12, 31);

    public async Task<IReadOnlyList<StockSheetRow>> GetSheetAsync(
        int tenantId, int warehouseId, int productId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        const string sql = """
            SELECT MovementId, OccurredAt, MovementType, QuantityDelta, RunningBalance, ReferenceNo
            FROM dbo.tvf_StockSheet(@TenantId, @WarehouseId, @ProductId, @From, @To)
            ORDER BY OccurredAt, MovementId;
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StockSheetRow>(new CommandDefinition(sql,
            new { TenantId = tenantId, WarehouseId = warehouseId, ProductId = productId, From = from ?? MinDate, To = to ?? MaxDate },
            cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StockOnHandRow>> GetOnHandAsync(int tenantId, DateTime? asOf, CancellationToken ct)
    {
        const string sql = """
            SELECT WarehouseId, WarehouseCode, ProductId, Sku, OnHand, TotalOnHand, SharePct
            FROM dbo.tvf_StockOnHand(@TenantId, @AsOf)
            ORDER BY Sku, WarehouseCode;
            """;
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<StockOnHandRow>(new CommandDefinition(sql,
            new { TenantId = tenantId, AsOf = asOf ?? MaxDate }, cancellationToken: ct));
        return rows.AsList();
    }
}
