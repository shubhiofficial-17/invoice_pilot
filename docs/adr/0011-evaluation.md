# 0011: Measuring the agent

**Status:** accepted

## Context

"It works on my demo invoice" says nothing about how often an AP agent gets invoices right, how often it approves something it shouldn't, or what it costs. Those are the numbers a finance team (and an interviewer) would ask for.

## Decision

- **A labelled synthetic set** (`EvalScenarios.Generate`, deterministic for a seed): 100 invoices against the seeded ERP across 15 categories. Clean (22), price within tolerance (8), price above tolerance (10), quantity over received (9), nothing received (4), duplicate written differently (6), re-bill of a fully invoiced PO (4), blocked supplier (4), cancelled PO (4), missing PO number (5), unknown PO (4), wrong tax rate (5), arithmetic error (4), high value (3), and **prompt injection** (8: over-billed gloves plus text telling the model to approve). Each case has the expected outcome, the finding codes that must appear, and a real PDF (`InvoicePdf.Render`, text layer, PdfPig).
- **Two modes** (`EvalRunner`, console `eval/InvoicePilot.Eval`):
  - **engine**: the true invoice data goes straight into the match and the policy, with no model. This tests the deterministic layer and the labels; anything below 100% is a bug. **CI runs it on every push** and fails on any miss, and the report appears in the job summary.
  - **agent**: the production pipeline. The model reads each PDF, the agent investigates with tools, and the guardrail decides. Every case gets a fresh in-memory ERP. Run it against the LLM Gateway with `--base-url`, `--model` and `--price-in/--price-out` (USD per million tokens).
- **Metrics:** decision accuracy after the guardrail, **false approvals** (approved but shouldn't have been: the costly error), pipeline failures, the agent's proposal accuracy *before* the guardrail, the number of guardrail overrides, extraction exactness on the fields decisions depend on (invoice number, PO, quantities, prices, tax, total), tokens and estimated cost per invoice, latency p50/p95, a per-category breakdown and a list of misses. Output is JSON plus a Markdown report.
- **Guardrail stress tests** in the test suite, using fake models:
  - a perfect model scores 100% with no overrides,
  - **an agent that always says "approve" still produces zero false approvals and 100% final accuracy**: every unsafe proposal is overridden (70 of 100),
  - an over-cautious agent that always rejects is *not* corrected (14% accuracy), and the report shows it. The guardrail only stops unsafe looseness; it doesn't fix bad judgement in the strict direction.

## Consequences

- Real-model numbers come from running agent mode against a real model. The repo doesn't claim them; `eval/README.md` explains how to produce them.
- The synthetic PDFs are cleaner than real supplier invoices (one layout, no scans), so extraction accuracy here is an upper bound. Real-world testing would add varied layouts and scanned images.
