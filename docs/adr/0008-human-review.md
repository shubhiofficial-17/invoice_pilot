# 0008: Human review, booking and the outbox

**Status:** accepted

## Context

Until now the system only recommends. Something has to turn a recommendation into an effect (an invoice booked for payment, an email to the supplier, a rejection), and in AP that must be a person's decision, with controls on what they can decide, safe under double-clicks, races and crashes.

## Decision

- **`POST /api/invoices/{id}/actions`** with `{ action, expectedVersion, outcome?, note? }`. Nothing changes state without it. Who acted, what, the outcome, the note and the time are appended to the invoice's `actions` list.
- **Reviewer actions** (invoice `awaitingReview`, or `investigationFailed` with a policy-only recommendation):
  - `confirm`: carry out the recommendation,
  - `override`: choose another outcome, **with a note**. It may be **stricter than the policy, never looser**: a reviewer can't approve what the policy holds or routes. That keeps segregation of duties: the person processing an invoice can't approve their own exception.
- **Approver actions** (invoice `awaitingApproval`, after a confirmed `route_for_approval`): `approve`, or `reject` with a note. **Nobody can approve what the policy rejects** (duplicate, blocked or unknown supplier, cancelled PO).
- **`expectedVersion`** is the version the person was looking at. A stale view, or a second click, gets **409**. Rule violations get **422**. Both come back as problem details.
- **Outcomes:**
  - approve → `approved`, and the invoice is **booked in the ERP** (`PostedInvoice` with `sourceRef` = this invoice's id),
  - route → `awaitingApproval` (the approver comes from the decision),
  - hold → `onHold`, with the supplier email in the invoice's **outbox**,
  - reject → `rejected`.
- **Booking is claim-then-book, and idempotent:**
  1. the version-checked update to `approved` is the claim, so only one person wins,
  2. then the ERP booking, whose key (supplier + normalised number) refuses duplicates,
  3. if the key already exists and its `sourceRef` is this invoice, that's an earlier attempt and counts as success; if it's another invoice, this one goes back to `awaitingReview` with the reason (409).
  - **`BookingReconciler`** runs on startup and completes any `approved` invoice whose booking was interrupted (a crash between steps 1 and 2). Running it twice is harmless.
- **Transactional outbox:** the email is written in the same document update as the `onHold` status, so "on hold" and "supplier emailed" can never disagree. `GET /api/outbox` lists queued emails; a real sender would read from there, send, and set `sentAt`. Sending is out of scope for the demo.

## Consequences

- The full lifecycle is now: upload → read → investigate → recommend → **a person decides** → booked, held, routed or rejected.
- The actor is a placeholder ("clerk") until sign-in arrives in milestone 11, which also restricts approver actions to the approver role.
