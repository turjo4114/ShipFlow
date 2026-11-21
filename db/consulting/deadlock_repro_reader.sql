/* Session B - "the monthly report". Seeks the nonclustered ledger index (takes S locks on index rows)
   then does a key lookup into the clustered index for Notes (needs S locks there).
   Writer holds X on the clustered row and wants X on the index row => lock cycle.
   The INDEX hint pins the plan that production statistics sometimes choose on their own. */
USE ShipFlow;
GO
SET NOCOUNT ON;
DECLARE @t INT = (SELECT TenantId FROM dbo.Tenants WHERE Code = N'ACME');
DECLARE @n INT = 0, @dead INT = 0, @x DECIMAL(18,3), @end DATETIME2 = DATEADD(SECOND, 90, SYSUTCDATETIME());
WHILE SYSUTCDATETIME() < @end
BEGIN
    BEGIN TRY
        SELECT @x = SUM(QuantityDelta), @n = @n + 0
        FROM dbo.StockMovements WITH (INDEX(IX_StockMovements_Ledger))
        WHERE TenantId = @t AND ProductId = 1 AND WarehouseId = 1 AND (Notes IS NULL OR Notes <> N'zzz');  -- Notes forces the key lookup
        SET @n += 1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() = 1205 SET @dead += 1; ELSE THROW;
    END CATCH
END
PRINT CONCAT('READER finished. Reports: ', @n, ', chosen as deadlock victim: ', @dead);
GO
