-- Rollback for enable_rcsi.sql (used for the before/after repro).
USE master;
GO
ALTER DATABASE ShipFlow SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;
GO
SELECT name, is_read_committed_snapshot_on FROM sys.databases WHERE name = N'ShipFlow';
GO
