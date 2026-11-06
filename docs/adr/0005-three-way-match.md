# 0005: Deterministic 3-way match

**Status:** accepted

## Context

The core AP check is the 3-way match: does the invoice agree with the purchase order (what we agreed to buy, at what price and tax) and the goods receipts (what actually arrived)? Around it sit supplier checks (known? blocked?) and duplicate checks. If a model did this arithmetic, the answers could change from run to run and couldn't be fully trusted.

## Decision

- **`MatchEngine.Run` is a pure function** of the extracted invoice and a `MatchContext` (suppliers, the PO, its receipts, the supplier's booked invoices). `MatchService` loads the context from `IErpStore`, then calls it. Same input, same report. The agent calls it as a tool; it never does the arithmetic itself.
- **The engine measures; the policy decides.** Each finding has a code, a severity, a message and the numbers behind it (expected, actual, variance %, amount impact). Severities:
  - `info`: billed below the PO price.
  - `warning`: price above the PO, payment terms shorter than agreed, supplier taken from the PO. The policy's tolerances decide these.
  - `exception`: needs a person or the supplier. Missing or unknown PO, closed PO, supplier not on the PO, currency mismatch, invoice line not on the PO, quantity above received or ordered, wrong tax rate or amount, PO value exceeded, possible duplicate, arithmetic errors on the invoice.
  - `block`: must not be paid. Unknown or blocked supplier, cancelled PO, exact duplicate.
- **Supplier resolution:** tax id first (ignoring spaces and punctuation), then name similarity (word overlap ≥ 0.6 after dropping "Pvt", "Ltd", "Inc" and similar). If neither is readable, the PO's supplier is used, with a warning.
- **Line matching:** SKU first, then description similarity (≥ 0.5). Quantities are summed per PO line before comparing with what was received and ordered, so an invoice that splits one PO line across two lines is still checked correctly.
- **Duplicates:** invoice numbers compare without case or punctuation (`AC/2026/0420` = `ac-2026-0420`). The same total within 3 days of a booked invoice is a *possible* duplicate. Booked amounts against the PO plus this invoice must not exceed the PO's gross value, which catches a re-bill under a new number.
- Tax is recomputed at the PO's rates. A tax-amount finding is suppressed when a tax-rate finding already explains it, so one cause gives one finding.
- `GET /api/invoices/{id}/match` computes the report fresh from the ERP each time.

## Consequences

- Each seeded scenario has a test, and the evaluation set (milestone 10) labels invoices by these finding codes.
- Booked invoices don't carry line quantities, so "already invoiced" is checked by amount against the PO, not per line.
