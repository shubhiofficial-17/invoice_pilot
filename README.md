# InvoicePilot

An accounts payable exception agent.

Large companies receive thousands of supplier invoices a month, and each one must be checked against its purchase order and goods receipt (a "3-way match"). Many don't match: wrong price, short quantity, duplicate invoice, wrong tax, missing PO number. Today a finance team fixes these by hand.

InvoicePilot does that work:

1. **Reads** an invoice PDF or image and extracts supplier, line items, totals and tax.
2. **Investigates** with tools: looks up the PO, the goods receipt, supplier master data and past invoices.
3. **Reasons** against a written AP policy (for example, "price variance under 2% auto-approves; above that, route to the buyer").
4. **Acts:** approves, puts on hold, drafts an email to the supplier, or routes to the right approver.
5. **Pauses for a human** before anything that moves money, and keeps a full audit trail of what it checked and why.

The LLM proposes; deterministic code checks the policy and decides what is allowed.

## Stack

Angular 18 (signals) · .NET 8 minimal APIs · MongoDB · Docker · GitHub Actions. LLM calls go through [LLM Gateway](https://github.com/shubhiofficial-17/llm_gateway).

## Running it

```bash
docker compose up --build
# open http://localhost:8080
```

For development:

```bash
cd server && dotnet run --project InvoicePilot.Api    # API on http://localhost:5080
cd web && npm install && npm start                     # web on http://localhost:4200
```

Tests: `dotnet test server/InvoicePilot.sln` and `cd web && npm run test:ci`.

## Milestones

| # | Milestone | Status |
|---|---|---|
| 1 | Skeleton: API, web, Mongo, Docker, CI | ✅ |
| 2 | Mock ERP: POs, goods receipts, suppliers, invoice history | ⏳ |
| 3 | Invoice ingestion and extraction | |
| 4 | 3-way match engine | |
| 5 | Agent loop (own tool calling, state, retries) | |
| 6 | AP policy and guardrails | |
| 7 | Actions and human approval checkpoints | |
| 8 | Audit trail | |
| 9 | AP Inbox UI | |
| 10 | Evaluation harness (synthetic invoice set) | |
| 11 | Hardening: roles, prompt-injection tests, E2E | |

Design decisions are recorded in [`docs/adr`](docs/adr).

## License

MIT
