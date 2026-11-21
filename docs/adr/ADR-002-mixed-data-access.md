# ADR-002: Mixed data access - EF Core for writes, Dapper + inline TVFs for analytics, scripts own the schema

- **Status:** Accepted
- **Date:** 2026-10-29

## Context
Two very different workloads: (a) transactional CRUD with concurrency control and atomic multi-row changes;
(b) read-heavy stock sheets that need windowed aggregates (`SUM() OVER`) and predictable plans.
LINQ expresses (b) poorly and tends to produce plans we cannot tune.

## Decision
- **EF Core** = write side and simple reads: change tracking, `rowversion` concurrency, one `SaveChanges` = one transaction (ledger row + outbox row commit atomically).
- **Dapper + hand-written T-SQL** = analytics reads. Logic lives in **inline table-valued functions** (`tvf_StockSheet`, `tvf_StockOnHand`): no scalar UDFs, no cursors, plan expands into the caller.
- **Schema is owned by versioned SQL scripts** (`db/init`), not EF migrations. Filtered indexes, composite FKs, window-friendly index key order and batched MERGE are first-class T-SQL; EF is only a mapper.

## Guardrails
- All Dapper SQL lives in `Repositories/*Reader` classes; each method requires `tenantId` (Dapper bypasses EF filters).
- Every TVF takes `@TenantId` as its first parameter.
- Integration tests assert tenant A never sees tenant B through **both** access paths.

## Consequences
- (+) Fast, tunable reports; ORM convenience where it pays off; DBAs can review/optimise SQL directly.
- (-) Two mappers to know; the model and the schema can drift. Mitigation: schema scripts are the single source of truth, plus a startup/CI smoke test against a real SQL Server.
