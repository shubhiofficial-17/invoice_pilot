# 0009: A tamper-evident audit trail

**Status:** accepted

## Context

Finance processes get audited. For every invoice, an auditor should be able to see what was uploaded, what the model read, which tools the agent called with what arguments, what it proposed, whether the policy overrode it, who confirmed what, and when it was booked. They should also be able to trust that this history wasn't edited afterwards, for example to hide that someone approved an exception.

## Decision

- **An append-only `IAuditLog`** (in-memory and MongoDB, one contract suite). The interface has no update or delete.
- **Events:** `invoice.received`, `extraction.completed` / `extraction.failed` / `extraction.requested`, one `agent.tool_call` per tool call (arguments, output, error flag, duration), `agent.proposed` / `agent.failed` / `agent.skipped` / `agent.requested` / `agent.result_discarded`, `policy.override`, `decision.recommended`, `human.<action>` (actor, outcome, note), `erp.booked`, `erp.booking_conflict`, `outbox.queued`. Each event has actor (a person, `system`, `agent` or `policy`), type, a one-line summary and string data. Long values are capped at 1,000 characters.
- **Hash chain per invoice:** `hash = SHA-256(canonical(invoiceId, seq, at, actor, type, summary, sorted data, prevHash))`, and the first event chains from 64 zeros. Editing any field of an old event, deleting one or reordering them breaks the chain from that point. Re-hashing the edited event doesn't help, because the next event still points at the old hash. `GET /api/invoices/{id}/audit` returns the events plus `verification: { intact, brokenAtSeq, count }`.
- **Concurrent appends:** a unique `(invoiceId, seq)` index. Two writers that read the same head collide, and the loser re-reads and retries with jitter. Tested with 20 parallel appends against real MongoDB.
- **Ordering with the state change:** the agent's events are written before the invoice's status changes, so anyone who sees `awaitingReview` can read the whole trace. If the result is then discarded (the invoice changed meanwhile), an `agent.result_discarded` event says so. Human actions are recorded after the state change succeeds, inside the same request.

## Consequences

- The trail is tamper-*evident*, not tamper-*proof*: someone with database access can still delete the whole chain or rewrite all of it. Anchoring the latest hash somewhere else (a daily digest emailed to compliance, or a write-once store) closes that gap. That's noted as a next step.
- The audit write isn't in the same transaction as the state change (MongoDB multi-document transactions need a replica set). A crash in between can lose an event, but the invoice document itself still holds the agent trace, the decision and the human actions, so the state stays explainable.
