# Run any script under .\db against the compose SQL Server.  Usage: .\scripts\run-sql.ps1 migration/legacy_demo_data.sql
param([Parameter(Mandatory = $true)][string]$File)
docker compose run --rm --no-deps -e "SQL_FILE=/db/$File" --entrypoint /bin/bash db-init `
  -c '/opt/mssql-tools18/bin/sqlcmd -C -I -b -S sqlserver -U sa -P "$MSSQL_SA_PASSWORD" -i "$SQL_FILE"'
