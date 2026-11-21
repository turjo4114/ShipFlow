/* =================================================================
   Non-blocking bulk migration: Legacy_StockMovements -> StockMovements
   - No cursor: a WHILE loop over dense key RANGES, each batch = one set-based MERGE.
   - Idempotent & resumable: unique (TenantId, LegacyId) + NOT MATCHED-only MERGE.
     Re-run it after a crash and it simply continues.
   - Short transactions: each batch commits on its own, so locks are held for milliseconds.
   - Batch size 1000: every inserted row takes locks on the clustered index + 2 nonclustered
     indexes (~3 locks/row). SQL Server escalates to a TABLE lock around 5,000 locks held by one
     statement, which would block every API writer. 1000 rows ~ 3,000 locks stays safely below it.
   - HOLDLOCK on the MERGE target serialises the existence check and insert (MERGE race safety);
     harmless at this batch size.
   - LOCK_TIMEOUT + DEADLOCK_PRIORITY LOW: the migration yields to live traffic, and retries
     deadlock/timeout victims (1205/1222) up to 3 times.
   - WAITFOR between batches lets log backups and API traffic breathe.
   ================================================================= */
USE ShipFlow;
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET LOCK_TIMEOUT 5000;
SET DEADLOCK_PRIORITY LOW;

IF OBJECT_ID(N'dbo.Legacy_Rejects', N'U') IS NULL
    CREATE TABLE dbo.Legacy_Rejects (LegacyRowId INT NOT NULL PRIMARY KEY, LegacyId NVARCHAR(64) NOT NULL, Reason VARCHAR(100) NOT NULL, RejectedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());

IF OBJECT_ID(N'dbo.MigrationLog', N'U') IS NULL
    CREATE TABLE dbo.MigrationLog (LogId INT IDENTITY PRIMARY KEY, RunId UNIQUEIDENTIFIER NOT NULL, FromKey INT NOT NULL, ToKey INT NOT NULL, RowsMerged INT NOT NULL, DurationMs INT NOT NULL, LoggedAt DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());

DECLARE @BatchSize INT = 1000,
        @DelayBetween CHAR(12) = '00:00:00.100',
        @RunId UNIQUEIDENTIFIER = NEWID(),
        @From INT, @To INT, @Max INT, @Rows INT, @Attempt INT = 0, @Total INT = 0, @t0 DATETIME2(3);

SELECT @From = MIN(LegacyRowId), @Max = MAX(LegacyRowId) FROM dbo.Legacy_StockMovements;
IF @From IS NULL
BEGIN
    PRINT 'Nothing to migrate.';
    RETURN;
END

-- 1) Park rows that cannot be mapped (one set-based insert; keeps the main loop clean)
INSERT dbo.Legacy_Rejects (LegacyRowId, LegacyId, Reason)
SELECT l.LegacyRowId, l.LegacyId,
       CASE WHEN t.TenantId IS NULL    THEN 'Unknown tenant'
            WHEN w.WarehouseId IS NULL THEN 'Unknown warehouse'
            WHEN p.ProductId IS NULL   THEN 'Unknown SKU'
            ELSE 'Invalid direction or zero quantity' END
FROM dbo.Legacy_StockMovements l
LEFT JOIN dbo.Tenants    t ON t.Code = l.TenantCode
LEFT JOIN dbo.Warehouses w ON w.TenantId = t.TenantId AND w.Code = l.WarehouseCode
LEFT JOIN dbo.Products   p ON p.TenantId = t.TenantId AND p.Sku  = l.Sku
WHERE (t.TenantId IS NULL OR w.WarehouseId IS NULL OR p.ProductId IS NULL
       OR ISNULL(l.Direction, '?') NOT IN ('I','O','A') OR l.Qty = 0)
  AND NOT EXISTS (SELECT 1 FROM dbo.Legacy_Rejects r WHERE r.LegacyRowId = l.LegacyRowId);
PRINT CONCAT('Rejected rows parked: ', @@ROWCOUNT);

-- 2) Batched MERGE
WHILE @From <= @Max
BEGIN
    SET @To = @From + @BatchSize - 1;
    SET @t0 = SYSUTCDATETIME();

    BEGIN TRY
        BEGIN TRAN;

        MERGE dbo.StockMovements WITH (HOLDLOCK) AS tgt
        USING (
            SELECT t.TenantId, w.WarehouseId, p.ProductId,
                   CASE l.Direction WHEN 'I' THEN 'RECEIPT' WHEN 'O' THEN 'ISSUE' ELSE 'ADJUSTMENT' END AS MovementType,
                   CASE l.Direction WHEN 'I' THEN ABS(l.Qty) WHEN 'O' THEN -ABS(l.Qty) ELSE l.Qty END   AS QuantityDelta,
                   l.RefNo, l.MovementDate, l.LegacyId
            FROM dbo.Legacy_StockMovements l
            JOIN dbo.Tenants    t ON t.Code = l.TenantCode
            JOIN dbo.Warehouses w ON w.TenantId = t.TenantId AND w.Code = l.WarehouseCode
            JOIN dbo.Products   p ON p.TenantId = t.TenantId AND p.Sku  = l.Sku
            WHERE l.LegacyRowId BETWEEN @From AND @To
              AND l.Direction IN ('I','O','A') AND l.Qty <> 0
        ) AS src
          ON tgt.TenantId = src.TenantId AND tgt.LegacyId = src.LegacyId
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (TenantId, WarehouseId, ProductId, MovementType, QuantityDelta, ReferenceNo, OccurredAt, LegacyId)
            VALUES (src.TenantId, src.WarehouseId, src.ProductId, src.MovementType, src.QuantityDelta, src.RefNo, src.MovementDate, src.LegacyId);

        SET @Rows = @@ROWCOUNT;
        COMMIT;

        INSERT dbo.MigrationLog (RunId, FromKey, ToKey, RowsMerged, DurationMs)
        VALUES (@RunId, @From, @To, @Rows, DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()));

        SET @Total += @Rows;
        SET @From  = @To + 1;
        SET @Attempt = 0;
        WAITFOR DELAY @DelayBetween;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;

        IF ERROR_NUMBER() IN (1205, 1222) AND @Attempt < 3      -- deadlock victim / lock timeout
        BEGIN
            SET @Attempt += 1;
            PRINT CONCAT('Batch ', @From, '-', @To, ' hit error ', ERROR_NUMBER(), '; retry ', @Attempt, '/3');
            WAITFOR DELAY '00:00:01';
            CONTINUE;                                           -- same batch again
        END;
        THROW;                                                  -- anything else: stop loudly
    END CATCH
END

PRINT CONCAT('Migration complete. Rows inserted this run: ', @Total, '. Run id: ', CONVERT(VARCHAR(36), @RunId));
SELECT COUNT(*) AS RejectedRows FROM dbo.Legacy_Rejects;
GO
