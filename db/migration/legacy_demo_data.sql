-- MANUAL. Creates the legacy staging table and fills it with 50,000 good rows + 25 bad rows,
-- so you can rehearse the batched migration. Run scripts/run-sql.* with: migration/legacy_demo_data.sql
USE ShipFlow;
GO
SET QUOTED_IDENTIFIER ON;
GO
IF OBJECT_ID(N'dbo.Legacy_StockMovements', N'U') IS NULL
CREATE TABLE dbo.Legacy_StockMovements
(
    LegacyRowId   INT IDENTITY(1,1) NOT NULL,   -- dense key used for range batching
    LegacyId      NVARCHAR(64)  NOT NULL,
    TenantCode    NVARCHAR(50)  NOT NULL,
    WarehouseCode NVARCHAR(50)  NOT NULL,
    Sku           NVARCHAR(64)  NOT NULL,
    MovementDate  DATETIME2(3)  NOT NULL,
    Qty           DECIMAL(18,3) NOT NULL,
    Direction     CHAR(1)       NULL,            -- I = in, O = out, A = signed adjustment
    RefNo         NVARCHAR(100) NULL,
    CONSTRAINT PK_Legacy_StockMovements PRIMARY KEY CLUSTERED (LegacyRowId),
    CONSTRAINT UQ_Legacy_TenantLegacyId UNIQUE (TenantCode, LegacyId)
);
GO
IF NOT EXISTS (SELECT 1 FROM dbo.Legacy_StockMovements)
BEGIN
    ;WITH n AS (
        SELECT TOP (50000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
        FROM sys.all_objects a CROSS JOIN sys.all_objects b
    )
    INSERT dbo.Legacy_StockMovements (LegacyId, TenantCode, WarehouseCode, Sku, MovementDate, Qty, Direction, RefNo)
    SELECT CONCAT(N'LEG-', i), N'ACME',
           CASE i % 2 WHEN 0 THEN N'WH-NYC' ELSE N'WH-LAX' END,
           CONCAT(N'SKU-', 1001 + (i % 5)),
           DATEADD(MINUTE, -i, SYSUTCDATETIME()),
           CAST(1 + (i % 50) AS DECIMAL(18,3)),
           CASE WHEN i % 3 = 0 THEN 'O' ELSE 'I' END,
           CONCAT(N'LEG-REF-', i)
    FROM n;

    -- 25 rows that cannot be mapped (unknown SKU) -> must land in Legacy_Rejects, not break the run
    ;WITH n AS (SELECT TOP (25) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects)
    INSERT dbo.Legacy_StockMovements (LegacyId, TenantCode, WarehouseCode, Sku, MovementDate, Qty, Direction, RefNo)
    SELECT CONCAT(N'BAD-', i), N'ACME', N'WH-NYC', N'SKU-DOES-NOT-EXIST', SYSUTCDATETIME(), 5, 'I', N'BAD'
    FROM n;
END
GO
