# 0006: A hand-written agent loop

**Status:** accepted

## Context

After an invoice is read, someone has to investigate: look up the PO, the receipts and the supplier, check for duplicates, apply the AP policy, and write a recommendation (or an email to the supplier). That is open-ended, so it suits a model with tools. But the loop around the model is where reliability comes from: what happens on bad arguments, outages, loops, runaway cost, or a model that never decides. Writing that loop myself is the point of the project.

## Decision

- **Tool calling** was added to `ILlmClient`: `LlmTool` (name, description, JSON Schema), `LlmToolCall` (id, name, raw JSON arguments), assistant messages that carry calls, and `tool` messages that answer a call id. `OpenAiCompatibleLlmClient` maps them to the OpenAI `tools`/`tool_calls` shape. Schemas are deep-cloned per request, because a `JsonNode` can only have one parent.
- **Eight tools** (`AgentTools.cs`), each a small class with a schema: `get_invoice`, `lookup_supplier`, `get_purchase_order`, `get_goods_receipts`, `get_supplier_invoices`, `run_three_way_match`, `read_ap_policy` and `submit_decision`. The ERP tools are read-only.
- **The loop** (`InvoiceAgent.RunAsync`): ask the model → run each requested tool → append the results → repeat until `submit_decision` succeeds. Guards:
  - **max 12 model calls** and **a 60k-token budget** per invoice,
  - **max 6 tool calls per turn**; extra calls still get an answer (the API requires every call id to be answered),
  - **retries on model outages** (1 s, 3 s), then the run fails cleanly,
  - **one nudge** if the model replies without calling a tool, then fail,
  - **repeat detection:** the same tool with the same arguments a third time gets an error instead of a result,
  - **tool errors go back to the model as data** (`{"error": ...}`), never as exceptions, so it can correct itself: unknown tool, invalid JSON, missing argument,
  - tool outputs are truncated to 8,000 characters.
- **`submit_decision` is the only way to finish, and it validates:**
  - it is refused until `run_three_way_match` has run, so no decision without the deterministic check,
  - the outcome must be `approve`, `route_for_approval`, `hold` or `reject`,
  - finding codes must exist in the match report (no invented reasons),
  - a hold needs an email to the supplier, and **the recipient comes from the ERP**, never from the model or the invoice (an invoice can't redirect the email).
- **The policy text the agent reads is generated from the same `ApPolicyOptions`** the code will enforce (`GET /api/policy` shows it), so prompt and rules can't drift apart.
- **The whole trace is kept:** every model call (text, tokens, time) and tool call (arguments, output, error flag, time), plus total usage and model calls. In MongoDB the trace is stored as a JSON document (via System.Text.Json → BSON). It contains no money, so the Decimal128 rule from ADR 0002 doesn't apply; records of value keep typed mappings.
- **Pipeline:** a successful read hands the invoice to `AgentWorker` (its own queue, 2 readers): `extracted → investigating → awaitingReview`, or `investigationFailed`. Each step is version-checked, and the worker resumes leftover work on startup. `POST /api/invoices/{id}/investigate` runs the agent again.

## Consequences

- The agent only **proposes**. Milestone 6 checks the proposal against the policy in code, and milestone 7 makes a person confirm it.
- Tests drive the loop with a scripted fake model (`AgentScript`), so every guard has a test and CI needs no API key.
