# 0013: Demo mode and end-to-end tests

**Status:** accepted

## Context

Two needs: anyone cloning the repo should be able to try the whole product without an API key, and CI should prove the pieces work together for real (nginx, cookies, the background workers, the UI), not just in-process.

## Decision

- **`DemoLlmClient`** (`Ai:Provider=demo`, the Docker Compose default when no `AI_BASE_URL` is set). It is **not an AI** and says so in its model name (`demo-rules (not an AI)`, shown in the UI and the audit trail):
  - as the reader, it parses the layout produced by `InvoicePdf` (the evaluation generator),
  - as the agent, it calls the real tools in the usual order (`get_invoice`, `run_three_way_match`, `read_ap_policy`) and submits what the policy requires, writing the hold email from the findings.
  - Everything else is the production code: upload, PDF text extraction, the agent loop, tool validation, the match, the guardrail, review, booking and audit. Only the model's judgement is replaced.
  - Tests check that it reads all 100 generated PDFs exactly, and that the full pipeline with it scores 100% with no failures.
- **Playwright end-to-end tests** (`e2e/`) run in CI against `docker compose up --build`, with sign-in on and the demo model. Invoice PDFs come from the evaluation generator (`--pdfs`). Journeys:
  1. signed-out visitors are sent to sign-in,
  2. the clerk uploads a clean invoice, waits for "Awaiting review", confirms, and sees "Approved", the ERP booking and a verified audit chain,
  3. a prompt-injection invoice ends "On hold", the UI won't offer "Approve", and the supplier email is queued,
  4. a price increase is routed to the PO's buyer; the clerk confirms, signs out, the buyer signs in and approves.
- The tests change state (bookings), so they run serially without retries. A trace, screenshots and the HTML report are uploaded when something fails.
- The CI `e2e` job replaces the earlier "build and check health" job: it builds the images, waits for `/api/health` through nginx, and then runs the browser tests.

## Consequences

- `docker compose up --build` gives a working demo with no keys. Setting `AI_BASE_URL` (and `AI_API_KEY`, `AI_MODEL`) switches to a real model through the LLM Gateway.
- Demo mode only reads the generator's PDF layout; real supplier invoices need a real model.
