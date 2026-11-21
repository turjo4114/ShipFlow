-- Least-privilege runtime login (stop using sa). Run with:  sqlcmd ... -v APP_PASSWORD="<strong password>" -i this.sql
USE master;
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'shipflow_app')
    CREATE LOGIN shipflow_app WITH PASSWORD = N'$(APP_PASSWORD)', CHECK_POLICY = ON;
GO
USE ShipFlow;
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'shipflow_app')
    CREATE USER shipflow_app FOR LOGIN shipflow_app;
ALTER ROLE db_datareader ADD MEMBER shipflow_app;
ALTER ROLE db_datawriter ADD MEMBER shipflow_app;
GRANT SELECT ON dbo.tvf_StockSheet  TO shipflow_app;
GRANT SELECT ON dbo.tvf_StockOnHand TO shipflow_app;
GO
