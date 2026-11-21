USE ShipFlow;
GO
SET QUOTED_IDENTIFIER ON;
GO
-- Dev seed (idempotent). Remove or gate this file for production.
INSERT dbo.Tenants (Code, Name)
SELECT v.Code, v.Name FROM (VALUES (N'ACME', N'Acme Logistics'), (N'GLOBEX', N'Globex Freight')) v(Code, Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Tenants t WHERE t.Code = v.Code);

DECLARE @acme INT = (SELECT TenantId FROM dbo.Tenants WHERE Code = N'ACME');
DECLARE @glob INT = (SELECT TenantId FROM dbo.Tenants WHERE Code = N'GLOBEX');

INSERT dbo.Warehouses (TenantId, Code, Name)
SELECT v.T, v.Code, v.Name
FROM (VALUES (@acme, N'WH-NYC', N'New York DC'), (@acme, N'WH-LAX', N'Los Angeles DC'), (@glob, N'WH-ROT', N'Rotterdam Hub')) v(T, Code, Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Warehouses w WHERE w.TenantId = v.T AND w.Code = v.Code);

INSERT dbo.Products (TenantId, Sku, Name)
SELECT v.T, v.Sku, v.Name
FROM (VALUES (@acme, N'SKU-1001', N'Pallet Wrap'), (@acme, N'SKU-1002', N'Cardboard Box L'),
             (@acme, N'SKU-1003', N'Packing Tape'), (@acme, N'SKU-1004', N'Bubble Mailer'),
             (@acme, N'SKU-1005', N'Label Roll'),   (@glob, N'SKU-2001', N'Container Seal')) v(T, Sku, Name)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.TenantId = v.T AND p.Sku = v.Sku);

IF NOT EXISTS (SELECT 1 FROM dbo.StockMovements WHERE TenantId = @acme AND ReferenceNo LIKE N'SEED-%')
BEGIN
    INSERT dbo.StockMovements (TenantId, WarehouseId, ProductId, MovementType, QuantityDelta, ReferenceNo, OccurredAt)
    SELECT @acme, w.WarehouseId, p.ProductId, 'RECEIPT', 100, N'SEED-RCPT', DATEADD(DAY, -30, SYSUTCDATETIME())
    FROM dbo.Warehouses w CROSS JOIN dbo.Products p WHERE w.TenantId = @acme AND p.TenantId = @acme;

    INSERT dbo.StockMovements (TenantId, WarehouseId, ProductId, MovementType, QuantityDelta, ReferenceNo, OccurredAt)
    SELECT @acme, w.WarehouseId, p.ProductId, 'ISSUE', -35, N'SEED-ISSUE', DATEADD(DAY, -10, SYSUTCDATETIME())
    FROM dbo.Warehouses w CROSS JOIN dbo.Products p WHERE w.TenantId = @acme AND p.TenantId = @acme;

    INSERT dbo.StockMovements (TenantId, WarehouseId, ProductId, MovementType, QuantityDelta, ReferenceNo, OccurredAt)
    SELECT @acme, w.WarehouseId, p.ProductId, 'ADJUSTMENT', -2, N'SEED-ADJ', DATEADD(DAY, -3, SYSUTCDATETIME())
    FROM dbo.Warehouses w CROSS JOIN dbo.Products p WHERE w.TenantId = @acme AND p.TenantId = @acme AND p.Sku = N'SKU-1001';
END
GO
