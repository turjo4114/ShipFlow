USE ShipFlow;
GO
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

/* ---------------------------------------------------------------
   Tenants: the SaaS boundary. Every other table carries TenantId.
---------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.Tenants', N'U') IS NULL
CREATE TABLE dbo.Tenants
(
    TenantId   INT           IDENTITY(1,1) NOT NULL,
    Code       NVARCHAR(50)  NOT NULL,
    Name       NVARCHAR(200) NOT NULL,
    IsActive   BIT           NOT NULL CONSTRAINT DF_Tenants_IsActive  DEFAULT (1),
    CreatedAt  DATETIME2(3)  NOT NULL CONSTRAINT DF_Tenants_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Tenants      PRIMARY KEY CLUSTERED (TenantId),
    CONSTRAINT UQ_Tenants_Code UNIQUE (Code)
);
GO

IF OBJECT_ID(N'dbo.Warehouses', N'U') IS NULL
CREATE TABLE dbo.Warehouses
(
    WarehouseId INT           IDENTITY(1,1) NOT NULL,
    TenantId    INT           NOT NULL,
    Code        NVARCHAR(50)  NOT NULL,
    Name        NVARCHAR(200) NOT NULL,
    IsActive    BIT           NOT NULL CONSTRAINT DF_Warehouses_IsActive  DEFAULT (1),
    CreatedAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_Warehouses_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Warehouses            PRIMARY KEY CLUSTERED (WarehouseId),
    CONSTRAINT FK_Warehouses_Tenants    FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId),
    CONSTRAINT UQ_Warehouses_TenantCode UNIQUE (TenantId, Code),
    CONSTRAINT UQ_Warehouses_TenantId   UNIQUE (TenantId, WarehouseId)   -- target of composite FKs
);
GO

IF OBJECT_ID(N'dbo.Products', N'U') IS NULL
CREATE TABLE dbo.Products
(
    ProductId     INT           IDENTITY(1,1) NOT NULL,
    TenantId      INT           NOT NULL,
    Sku           NVARCHAR(64)  NOT NULL,
    Name          NVARCHAR(200) NOT NULL,
    UnitOfMeasure NVARCHAR(16)  NOT NULL CONSTRAINT DF_Products_Uom       DEFAULT (N'EA'),
    IsActive      BIT           NOT NULL CONSTRAINT DF_Products_IsActive  DEFAULT (1),
    CreatedAt     DATETIME2(3)  NOT NULL CONSTRAINT DF_Products_CreatedAt DEFAULT (SYSUTCDATETIME()),
    RowVersion    ROWVERSION    NOT NULL,
    CONSTRAINT PK_Products          PRIMARY KEY CLUSTERED (ProductId),
    CONSTRAINT FK_Products_Tenants  FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId),
    CONSTRAINT UQ_Products_TenantSku UNIQUE (TenantId, Sku),
    CONSTRAINT UQ_Products_TenantId  UNIQUE (TenantId, ProductId)        -- target of composite FKs
);
GO

/* ---------------------------------------------------------------
   StockMovements: append-only signed ledger. On-hand is DERIVED, never stored.
   Clustered on the identity (append-only = no page splits);
   the covering nonclustered index below serves the ledger reads.
   Composite FKs make cross-tenant references physically impossible.
---------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NULL
CREATE TABLE dbo.StockMovements
(
    MovementId    BIGINT        IDENTITY(1,1) NOT NULL,
    TenantId      INT           NOT NULL,
    WarehouseId   INT           NOT NULL,
    ProductId     INT           NOT NULL,
    MovementType  VARCHAR(20)   NOT NULL,
    QuantityDelta DECIMAL(18,3) NOT NULL,           -- signed: + into stock, - out of stock
    ReferenceNo   NVARCHAR(100) NULL,
    Notes         NVARCHAR(500) NULL,
    OccurredAt    DATETIME2(3)  NOT NULL,
    CreatedAt     DATETIME2(3)  NOT NULL CONSTRAINT DF_StockMovements_CreatedAt DEFAULT (SYSUTCDATETIME()),
    LegacyId      NVARCHAR(64)  NULL,               -- idempotency key for legacy migration
    CONSTRAINT PK_StockMovements PRIMARY KEY CLUSTERED (MovementId),
    CONSTRAINT FK_StockMovements_Warehouse FOREIGN KEY (TenantId, WarehouseId) REFERENCES dbo.Warehouses (TenantId, WarehouseId),
    CONSTRAINT FK_StockMovements_Product   FOREIGN KEY (TenantId, ProductId)   REFERENCES dbo.Products   (TenantId, ProductId),
    CONSTRAINT CK_StockMovements_Type CHECK (MovementType IN ('RECEIPT','ISSUE','ADJUSTMENT','TRANSFER_IN','TRANSFER_OUT')),
    CONSTRAINT CK_StockMovements_Sign CHECK (
          (MovementType IN ('RECEIPT','TRANSFER_IN')  AND QuantityDelta > 0)
       OR (MovementType IN ('ISSUE','TRANSFER_OUT')   AND QuantityDelta < 0)
       OR (MovementType = 'ADJUSTMENT'                AND QuantityDelta <> 0))
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_Ledger' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
CREATE NONCLUSTERED INDEX IX_StockMovements_Ledger
    ON dbo.StockMovements (TenantId, ProductId, WarehouseId, OccurredAt, MovementId)
    INCLUDE (QuantityDelta, MovementType, ReferenceNo);
-- Key order = partition (Tenant, Product, Warehouse) then the window ORDER BY (OccurredAt, MovementId):
-- SUM() OVER can stream from this index with no Sort operator.
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_StockMovements_Legacy' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
CREATE UNIQUE NONCLUSTERED INDEX UX_StockMovements_Legacy
    ON dbo.StockMovements (TenantId, LegacyId)
    WHERE LegacyId IS NOT NULL;
GO

/* ---------------------------------------------------------------
   Transactional outbox (Phase 3 background processing)
   Status: 0 pending | 1 processing | 2 done | 3 dead-lettered
---------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.OutboxMessages', N'U') IS NULL
CREATE TABLE dbo.OutboxMessages
(
    OutboxMessageId BIGINT        IDENTITY(1,1) NOT NULL,
    TenantId        INT           NOT NULL,
    MessageType     VARCHAR(60)   NOT NULL,
    Payload         NVARCHAR(MAX) NOT NULL,
    Status          TINYINT       NOT NULL CONSTRAINT DF_Outbox_Status   DEFAULT (0),
    Attempts        INT           NOT NULL CONSTRAINT DF_Outbox_Attempts DEFAULT (0),
    NextAttemptAt   DATETIME2(3)  NOT NULL CONSTRAINT DF_Outbox_Next     DEFAULT (SYSUTCDATETIME()),
    LockedUntil     DATETIME2(3)  NULL,
    LastError       NVARCHAR(1000) NULL,
    CreatedAt       DATETIME2(3)  NOT NULL CONSTRAINT DF_Outbox_Created  DEFAULT (SYSUTCDATETIME()),
    ProcessedAt     DATETIME2(3)  NULL,
    CONSTRAINT PK_OutboxMessages PRIMARY KEY CLUSTERED (OutboxMessageId),
    CONSTRAINT FK_OutboxMessages_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (TenantId)
);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OutboxMessages_Dispatch' AND object_id = OBJECT_ID(N'dbo.OutboxMessages'))
CREATE NONCLUSTERED INDEX IX_OutboxMessages_Dispatch
    ON dbo.OutboxMessages (Status, NextAttemptAt) INCLUDE (LockedUntil);
GO
