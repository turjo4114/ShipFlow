#!/usr/bin/env bash
# Run any script under ./db against the compose SQL Server.  Usage: scripts/run-sql.sh migration/legacy_demo_data.sql
set -euo pipefail
f="${1:?usage: run-sql.sh <path relative to db/>}"
docker compose run --rm --no-deps -e "SQL_FILE=/db/$f" --entrypoint /bin/bash db-init \
  -c '/opt/mssql-tools18/bin/sqlcmd -C -I -b -S sqlserver -U sa -P "$MSSQL_SA_PASSWORD" -i "$SQL_FILE"'
