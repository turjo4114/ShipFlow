/* Session A - "the order system". Run for 90s alongside deadlock_repro_reader.sql.
   Each iteration updates the SAME row's clustered record and nonclustered ledger index entry
   (QuantityDelta is an INCLUDE column), in the order: clustered index first, nonclustered second. */
USE ShipFlow;
GO
SET NOCOUNT ON;
DECLARE @min BIGINT, @max BIGINT, @id BIGINT, @n INT = 0, @dead INT = 0, @end DATETIME2 = DATEADD(SECOND, 90, SYSUTCDATETIME());
SELECT @min = MIN(MovementId), @max = MAX(MovementId) FROM dbo.StockMovements;
WHILE SYSUTCDATETIME() < @end
BEGIN
    SET @id = @min + ABS(CHECKSUM(NEWID())) % (@max - @min + 1);
    BEGIN TRY
        BEGIN TRAN;
        UPDATE dbo.StockMovements SET QuantityDelta = QuantityDelta + 1 WHERE MovementId = @id AND MovementType = 'RECEIPT';
        UPDATE dbo.StockMovements SET QuantityDelta = QuantityDelta - 1 WHERE MovementId = @id AND MovementType = 'RECEIPT';
        COMMIT;
        SET @n += 1;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        IF ERROR_NUMBER() = 1205 SET @dead += 1; ELSE THROW;
    END CATCH
END
PRINT CONCAT('WRITER finished. Transactions: ', @n, ', chosen as deadlock victim: ', @dead);
GO
