# ADR-003: Enable READ_COMMITTED_SNAPSHOT (RCSI)

- **Status:** Accepted (applied after the monthly-report incident - see `docs/consulting/deadlock-case-study.md`)
- **Date:** 2026-10-29

## Context
Month-end reports run long `SELECT`s against `StockMovements` while the order system keeps writing. Under the
default locking READ COMMITTED, readers take shared locks. A reader seeking `IX_StockMovements_Ledger` and then
doing a key lookup into the clustered index (for `Notes`) can form a lock cycle with a writer that updates the
clustered row and then the index row. Result: intermittent error 1205 for the client during month-end.

## Decision
`ALTER DATABASE ShipFlow SET READ_COMMITTED_SNAPSHOT ON`. Readers use row versions from tempdb; they no longer
block writers or join lock cycles. `ALLOW_SNAPSHOT_ISOLATION` is left OFF until a use case needs it.

## Alternatives
- **Covering index (INCLUDE Notes):** removes this one cycle but not the class of problem, and widens every write. Kept as a possible complement, not the fix.
- **`NOLOCK` hints:** rejected. Dirty reads and double/missed rows would make financial-grade stock sheets wrong.
- **Retry only:** EF's retrying strategy already retries 1205, but retries hide a design flaw and make the report slower.

## Consequences
- (+) Reports stop deadlocking and stop blocking order entry; no application changes required.
- (-) tempdb version store grows with long-running transactions. Mitigation: monitor `sys.dm_tran_version_store_space_usage`, keep transactions short, size tempdb.
- (-) +14 bytes per modified row; slightly more write overhead.
- (-) Code that relied on readers *blocking* behind writers must now take explicit locks (`UPDLOCK`) for read-then-write patterns. Audit before rollout.
- Enabling needs exclusive access: schedule a window (`WITH ROLLBACK IMMEDIATE` drops open sessions).
