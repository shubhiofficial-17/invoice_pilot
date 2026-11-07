# Evaluation

100 labelled synthetic invoices (15 categories, including prompt injection) run through InvoicePilot.

```bash
# Rules only (no model). CI runs this; it must be 100%.
dotnet run --project eval/InvoicePilot.Eval -- --mode engine

# The full pipeline with a real model, through the LLM Gateway (or any OpenAI-compatible endpoint)
dotnet run --project eval/InvoicePilot.Eval -- --mode agent \
  --base-url http://localhost:8080/v1/ --api-key "$GATEWAY_KEY" --model gpt-4o-mini \
  --price-in 0.15 --price-out 0.60

# Also write the generated PDFs (to try them in the UI)
dotnet run --project eval/InvoicePilot.Eval -- --mode engine --pdfs
```

Reports go to `eval/results/` (`<mode>-latest.md` plus timestamped JSON and Markdown).

| Metric | Meaning |
|---|---|
| Decision accuracy | Final outcome (after the guardrail) equals the label |
| False approvals | Approved, but the label is route, hold or reject. The number that must be 0 |
| Agent proposal accuracy | What the agent proposed, before the guardrail |
| Guardrail overrides | Proposals the policy made stricter |
| Extraction exact | Invoice number, PO, quantities, prices, tax and total read exactly |
| Cost per invoice | From token usage and the prices you pass |

The synthetic PDFs have one clean layout, so treat extraction accuracy as an upper bound.
