# Case study: "Month-end reports keep failing" - deadlocks and RCSI

*A worked example of translating a database fix into business language. Use it as a template for client escalations.*

## 1. The situation
**Client:** Acme Logistics (ACME tenant). **Escalation:** the finance director emails the account lead:
> "Every month-end our stock reports fail with some database error, and the warehouse says order entry freezes
> while finance runs them. We close the books in 3 days. This is the third month in a row."

**What the client needs to hear first** (before any technical detail):
1. We understand the deadline and the cost of repeated failures.
2. We have a named owner and a time for the next update.
3. We will tell them what we found in plain language, not blame them or the report.

## 2. Opening the conversation (empathetic, concrete)
> "Thank you for flagging this, and I'm sorry you've hit it three months running with close approaching. I'm taking
> ownership today. I'll reproduce the failure on a copy of your data this morning, and I'll update you by 3 pm with what
> I find and a plan that doesn't put your close at risk. Could you send me one report run time that failed last month?"

Avoid: "it's a known SQL Server behaviour", "the report is badly written", "can't reproduce". Name the impact, not the jargon.

## 3. Investigation (what we actually do)
1. **Get the evidence:** error text is *"Transaction was deadlocked on lock resources ... chosen as the deadlock victim" (1205)*.
2. **Pull the deadlock graph** from the always-on `system_health` session: `db/consulting/read_deadlock_graph.sql`.
3. **Read it:** two processes. The report (`SELECT` on `StockMovements`) holds a **shared lock on an index row** and waits for the **clustered row**; the order-entry update holds the **exclusive lock on the clustered row** and waits for the **index row**. Each waits on the other: a cycle; SQL Server kills one.
4. **Reproduce on demand:** run `deadlock_repro_writer.sql` and `deadlock_repro_reader.sql` in two sessions for 90 seconds on the seeded + migrated data (`legacy_demo_data.sql` first for volume). Expect both to report victims.
5. **Why month-end only:** the reports run for minutes instead of milliseconds, so the window for a collision becomes large.

## 4. Root cause (one sentence for the client)
> "While finance's report is reading, it briefly locks rows that order entry needs to change, and sometimes the two end
> up each waiting for the other; the database resolves that by cancelling one of them."

## 5. Options we offered
| Option | Effect | Risk / cost |
|---|---|---|
| **A. Enable RCSI** (recommended) | Readers see a consistent snapshot instead of locking; reports and order entry stop interfering | Extra tempdb usage; 2 min maintenance window; audit code that relies on readers blocking |
| B. Covering index (`INCLUDE Notes`) | Removes this specific lookup cycle | Doesn't fix the class of problem; wider index slows every write |
| C. Retry on failure | Hides the error | Report still slow; doesn't help order entry; treats the symptom |
| D. `NOLOCK` | Avoids locks | **Rejected:** can return wrong totals. Unacceptable for stock sheets |

## 6. The fix and rollout plan
1. **Baseline (T-7 days):** record deadlock count and report duration from the `system_health` session / Query Store.
2. **Staging rehearsal:** `enable_rcsi.sql` on a restored copy; re-run the two repro scripts. **Expected: 0 victims.** Disable again with `disable_rcsi.sql` to prove the before/after.
3. **Production window (off-hours):** `enable_rcsi.sql` (`WITH ROLLBACK IMMEDIATE` ends open sessions for a few seconds). Confirm `is_read_committed_snapshot_on = 1`.
4. **Watch tempdb version store** during the next large report: `SELECT * FROM sys.dm_tran_version_store_space_usage;`
5. **Keep** application retry for 1205 as a safety net (EF `EnableRetryOnFailure` already does).
6. **Follow-up:** audit read-then-write code paths; add `UPDLOCK` where they depended on blocking reads.

## 7. Validation (what "done" means)
- Month-end reports complete **with zero 1205 errors** over two cycles.
- Order-entry p95 latency during report execution within +/- 10% of normal.
- tempdb version store peak recorded and within capacity.

## 8. Closing message to the client
> **Subject: Month-end report failures - resolved and verified**
>
> Hi [Name],
>
> Quick summary of what happened and what we did.
>
> **Cause:** during long reports, the database occasionally locked the same records as order entry in opposite order, so it cancelled one of the two. It only showed at month-end because the reports run longest then.
>
> **Fix:** we switched the database to a "snapshot read" mode, so reports read a stable copy and never wait on order entry. We rehearsed this on a copy of your data first: before the change the problem reproduced within a minute; after it, zero failures over the same test.
>
> **Impact on you:** a 2-minute maintenance window tonight at 11 pm; nothing to change on your side. Reports and order entry can now run at the same time.
>
> **What we'll watch:** deadlock count (target 0), report duration, and database memory use after your next close. I'll send you the numbers on [date].
>
> If anything looks off before then, call me directly on [number].

## 9. Lessons for the team
- Lead with impact and ownership, then facts, then technical detail on request.
- Reproduce before you fix; prove with a before/after.
- Pair every fix with its trade-off (here: tempdb) - honesty builds trust.
- Record the decision (ADR-003) so the next engineer knows *why*.
