# 0001: Stack and repository layout

**Status:** accepted

## Context

InvoicePilot is an accounts payable (AP) exception agent with a web UI for the finance team. It needs:

- an API that owns the agent loop, the AP policy and the audit trail,
- a mock ERP (purchase orders, goods receipts, suppliers, past invoices) for the agent's tools to query,
- a browser app where AP clerks see the inbox, read the agent's reasoning and approve or reject.

## Decision

- **API:** .NET 8 minimal APIs (`server/InvoicePilot.Api`). The agent loop is written by hand in C#, with no agent framework, so every step, retry and guardrail is visible and testable.
- **Web:** Angular 18 with standalone components, signals and `OnPush` change detection (`web/`).
- **Storage:** MongoDB. ERP records, invoices and agent runs are documents with nested line items and steps, so a document store fits them without joins.
- **One origin:** in Docker, nginx serves the Angular build and proxies `/api` to the API. The browser talks to one host, so no CORS is needed and cookie auth stays simple later.
- **LLM calls** will go through an OpenAI-compatible endpoint, normally my [LLM Gateway](https://github.com/shubhiofficial-17/llm_gateway). The agent only depends on an `ILlmClient` interface, so tests use a scripted fake and CI needs no API key.
- **CI:** GitHub Actions with three jobs: `server` (xUnit), `web` (Karma in headless Chrome, then a production build) and `docker` (build the stack, start it and check `/api/health` through nginx).
- Errors are returned as RFC 7807 problem details (`application/problem+json`).

## Consequences

- Both the API and the web app can run alone during development (`dotnet run` on :5080, `ng serve` proxying `/api`).
- The nginx config already sets a Content-Security-Policy without inline scripts. Angular's critical-CSS inlining is turned off because it injects an inline `onload` handler that the policy would block.
- No lockfile is committed yet. Dependencies use caret ranges within Angular 18.
