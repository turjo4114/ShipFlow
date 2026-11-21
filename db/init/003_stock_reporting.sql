USE ShipFlow;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------
   Stock sheet: running balance per (tenant, warehouse, product).
   - Windowed SUM() OVER: one ordered pass, no cursor, no scalar UDF
     (scalar UDFs run row-by-row and block parallelism).
   - Inline TVF = expanded into the caller's plan like a parameterised view.
   - Date filter is applied AFTER the window so the opening balance is right;
     the upper bound can safely be pushed below the window.
---------------------------------------------------------------- */
CREATE OR ALTER FUNCTION dbo.tvf_StockSheet
(
    @TenantId    INT,
    @WarehouseId INT,
    @ProductId   INT,
    @From        DATETIME2(3),
    @To          DATETIME2(3)
)
RETURNS TABLE
AS
RETURN
(
    WITH ledger AS
    (
        SELECT  sm.MovementId,
                sm.OccurredAt,
                sm.MovementType,
                sm.QuantityDelta,
                sm.ReferenceNo,
                SUM(sm.QuantityDelta) OVER (
                    PARTITION BY sm.TenantId, sm.WarehouseId, sm.ProductId
                    ORDER BY sm.OccurredAt, sm.MovementId
                    ROWS UNBOUNDED PRECEDING) AS RunningBalance
        FROM    dbo.StockMovements sm
        WHERE   sm.TenantId    = @TenantId
          AND   sm.WarehouseId = @WarehouseId
          AND   sm.ProductId   = @ProductId
          AND   sm.OccurredAt <= @To
    )
    SELECT MovementId, OccurredAt, MovementType, QuantityDelta, RunningBalance, ReferenceNo
    FROM   ledger
    WHERE  OccurredAt >= @From
);
GO

/* ---------------------------------------------------------------
   On-hand snapshot as of a point in time, with a second window over the
   aggregate to show each warehouse's share of the product's total stock.
---------------------------------------------------------------- */
CREATE OR ALTER FUNCTION dbo.tvf_StockOnHand
(
    @TenantId INT,
    @AsOf     DATETIME2(3)
)
RETURNS TABLE
AS
RETURN
(
    SELECT  sm.WarehouseId,
            w.Code AS WarehouseCode,
            sm.ProductId,
            p.Sku,
            SUM(sm.QuantityDelta)                                  AS OnHand,
            SUM(SUM(sm.QuantityDelta)) OVER (PARTITION BY sm.ProductId) AS TotalOnHand,
            CAST(100.0 * SUM(sm.QuantityDelta)
                 / NULLIF(SUM(SUM(sm.QuantityDelta)) OVER (PARTITION BY sm.ProductId), 0)
                 AS DECIMAL(9,2))                                  AS SharePct
    FROM    dbo.StockMovements sm
    JOIN    dbo.Warehouses w ON w.TenantId = sm.TenantId AND w.WarehouseId = sm.WarehouseId
    JOIN    dbo.Products   p ON p.TenantId = sm.TenantId AND p.ProductId   = sm.ProductId
    WHERE   sm.TenantId = @TenantId
      AND   sm.OccurredAt <= @AsOf
    GROUP BY sm.WarehouseId, w.Code, sm.ProductId, p.Sku
);
GO
