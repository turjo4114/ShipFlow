# ADR-001: Shared database, shared schema, `TenantId` discriminator

- **Status:** Accepted
- **Date:** 2026-10-29
- **Decision makers:** Architecture lead; reviewed with product

## Context
ShipFlow is a SaaS platform for 3PL / warehouse operators. We expect many small-to-mid tenants, fast onboarding
(minutes, not days), a small ops team, and the need to run cross-tenant operational analytics (capacity, billing).
Tenant data must never leak between customers.

## Decision
One SQL Server database, one schema. Every business table carries `TenantId`, and:
1. `TenantId` is the **leading column** of every business index (`IX_StockMovements_Ledger`, unique `(TenantId, Sku)`, ...).
2. **Composite foreign keys** `(TenantId, WarehouseId)` / `(TenantId, ProductId)` make cross-tenant references
   physically impossible, even if application code has a bug.
3. EF Core **global query filters** scope every ORM read; `SaveChanges` stamps `TenantId` and blocks cross-tenant writes.
4. Dapper reads go through reader classes where `tenantId` is a **required parameter** of every inline TVF (ADR-002).
5. Tenant is resolved once per request in middleware (`X-Tenant-Code` in dev; a validated JWT claim in production).

## Alternatives considered
| Option | Why not (now) |
|---|---|
| Database per tenant | Strongest isolation and per-tenant restore, but N x migrations, fragmented connection pools, higher cost, slow onboarding. Over-spec for our tenant profile. |
| Schema per tenant | Plan-cache bloat, migration fan-out, and still one instance. Complexity without the isolation benefits of separate DBs. |

## Consequences
- (+) Cheapest to run, one migration path, trivial onboarding, simple cross-tenant reporting.
- (-) Noisy-neighbour risk; one bad query can affect all tenants. Mitigation: tenant-leading indexes, query review, Query Store monitoring.
- (-) A forgotten filter would leak data. Mitigation: composite FKs, filters by default, tenant-isolation tests, and **Row-Level Security** (`SESSION_CONTEXT`) as the planned defence-in-depth step.
- **Revisit when:** a tenant requires data residency/contractual isolation, or exceeds ~25% of total load. The identical schema makes promoting a tenant to a dedicated database a copy-by-`TenantId` job.
