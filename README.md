# ShipFlow

Multi-tenant shipment & inventory platform. .NET 8 API · SQL Server 2022 · Docker.

## Quick start
```bash
cp .env.example .env        # then edit the password
docker compose up --build
```
- Swagger:  http://localhost:8080/swagger
- Liveness: http://localhost:8080/health/live
- Readiness (proves API → SQL via container hostname): http://localhost:8080/health/ready
- SSMS / Azure Data Studio (dev override): `localhost,1433`, user `sa`

## Production (no DB port exposed)
```bash
docker compose -f docker-compose.yml up -d --build
```

## Local scaffolding (if starting from scratch)
```bash
dotnet new sln -n ShipFlow
dotnet sln add src/ShipFlow.Api/ShipFlow.Api.csproj
git init && git add . && git commit -m "Phase 1: infra + docker"
```

Multi-tenant shipment & inventory platform. .NET 8 API · EF Core + Dapper · SQL Server 2022 · Docker.

**Start here: [guide.txt](guide.txt)** (setup, configuration, demos, hardening checklist).

```bash
cp .env.example .env     # set MSSQL_SA_PASSWORD
docker compose up --build
# http://localhost:8080/swagger   (header: X-Tenant-Code: ACME)
```

| Phase | What | Where |
|---|---|---|
| 1 Infrastructure | Multi-stage Dockerfile, Compose, isolated networks, health checks | `Dockerfile`, `docker-compose*.yml` |
| 2 Database | Tenant-safe schema, window-function stock sheets, batched MERGE migration | `db/init`, `db/migration` |
| 3 API | EF Core + repositories, Polly resilience, persistent outbox worker | `src/ShipFlow.Api` |
| 4 Consulting | ADRs + RCSI deadlock case study with repro scripts | `docs/`, `db/consulting` |
