-- Idempotent. RCSI is deliberately OFF here: Phase 4's case study enables it (db/consulting/enable_rcsi.sql).
IF DB_ID(N'ShipFlow') IS NULL
BEGIN
    CREATE DATABASE ShipFlow;
END
GO
ALTER DATABASE ShipFlow SET RECOVERY SIMPLE;   -- dev default; use FULL + log backups in production
GO
