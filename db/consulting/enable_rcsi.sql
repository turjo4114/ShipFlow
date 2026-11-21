/* The fix for the monthly-report deadlocks. READ_COMMITTED_SNAPSHOT makes READ COMMITTED readers
   use row versions (tempdb version store) instead of shared locks, so readers neither block
   writers nor take part in lock cycles. Run in a maintenance window: ROLLBACK IMMEDIATE kills open sessions. */
USE master;
GO
ALTER DATABASE ShipFlow SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO
SELECT name, is_read_committed_snapshot_on, snapshot_isolation_state_desc
FROM sys.databases WHERE name = N'ShipFlow';
GO
