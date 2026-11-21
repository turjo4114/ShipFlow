# ADR-004: Transactional outbox + Polly resilience pipeline for carrier integration

- **Status:** Accepted
- **Date:** 2026-10-29

## Context
Stock changes must notify an external carrier API that we do not control (slow, flaky, rate-limited). A synchronous
call inside the request would couple our availability to theirs; a fire-and-forget in-memory queue would lose events on restart.

## Decision
1. **Transactional outbox:** the event row is written in the *same* `SaveChanges` as the ledger row (`OutboxMessages`).
2. **`OutboxProcessor` (BackgroundService)** drains it: atomic claim via `UPDLOCK, READPAST`, expired claims are re-claimed (crash recovery), per-message exponential backoff, dead-letter after `MaxAttempts`.
3. **HttpClient resilience pipeline** (Polly v8 via `Microsoft.Extensions.Http.Resilience`): total timeout -> retry (4x, exponential + jitter) -> circuit breaker (50% failures, min 8 calls/30s, 30s break) -> per-attempt timeout.
4. **At-least-once delivery** + `Idempotency-Key: shipflow-outbox-{id}` so the carrier can de-duplicate.

## Alternatives
- In-memory `Channel<T>` only: fast but not persistent. Rejected.
- Message broker (Service Bus/RabbitMQ): right next step at higher volume; the outbox table is the seam where it plugs in.
- Legacy `Microsoft.Extensions.Http.Polly`: superseded by the resilience package.

## Consequences
- (+) API latency and availability are independent of the carrier; nothing is lost on crash; carrier outages degrade to "queued".
- (-) Eventual consistency: carrier state lags. Surface via `GET /api/ops/outbox`.
- (-) Duplicates are possible by design; the carrier contract must honour idempotency keys.
- Operations: alert on `dead` > 0 and on queue age.
