# 0002: Mock ERP

**Status:** accepted

## Context

The agent can't check an invoice without the data a real AP team would look up: the purchase order (what was ordered, at what price and tax), the goods receipts (what actually arrived), the supplier master (is the supplier active, what currency, where to send emails) and the invoices already booked (to catch duplicates). A real ERP (SAP, Oracle, Dynamics) is out of reach, so InvoicePilot has its own small one.

## Decision

- **Records:** `Supplier`, `PurchaseOrder` (with `PoLine`s), `GoodsReceipt` (with `ReceiptLine`s pointing at PO line numbers) and `PostedInvoice`. They are immutable C# records. Money is `decimal`, and tax rates are fractions (`0.18`).
- **`IErpStore`** has two implementations: `InMemoryErpStore` for tests and quick runs, and `MongoErpStore` when `ConnectionStrings:Mongo` is set.
- **One contract test suite** (`ErpStoreContract`) runs against both. The Mongo run uses Testcontainers to start a real `mongo:7.0` in the test run, so CI tests the real driver mapping.
- **Separate Mongo document classes** (`*Doc`) instead of putting attributes on the domain records:
  - money is always stored as **Decimal128**, never as a double (rounding errors) or a string (can't be summed in queries),
  - enums are stored as strings, so the data is readable and doesn't break if the enum order changes,
  - dates are stored as UTC midnight and come back as `DateOnly`.
- **Keys are normalised:** stored upper-case, and lookups ignore case and spaces. The agent (an LLM) may ask for `po-1001 `, and that should still find `PO-1001`.
- **One booking per supplier and invoice number:** the posted invoice's `_id` is `"{supplier}|{NUMBER}"`, so the database itself refuses a duplicate booking (`AddPostedInvoiceAsync` returns `false`). The same number from two different suppliers is allowed.
- **Demo data** (`ErpSeed`) is loaded at startup when the ERP is empty (turn off with `Erp:Seed=false`). Each record exists for a scenario later milestones need: a clean match, two tax rates on one PO, a USD PO received in two deliveries, a partial delivery (300 of 500), a blocked supplier, a cancelled PO, a PO with nothing received yet, and an already-booked invoice to resubmit as a duplicate.
- **Read-only endpoints** under `/api/erp` for the UI and for debugging: suppliers, a supplier's invoice history, purchase orders (filterable by supplier), one PO, and a PO's receipts. Unknown records return a 404 problem.

## Consequences

- The agent's tools (milestone 5) will be thin wrappers over `IErpStore`, so they can be tested with the in-memory store.
- Changing the seed changes what the evaluation set expects. The scenarios are documented in `ErpSeed.cs` so they stay stable.
- There is no write API for the ERP. Only the agent's approval action (milestone 7) books invoices.
