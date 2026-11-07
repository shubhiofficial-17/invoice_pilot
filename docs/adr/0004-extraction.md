# 0004: Reading invoices with an LLM

**Status:** accepted

## Context

An uploaded invoice is a PDF or an image. The agent needs structured data: supplier, invoice number, dates, PO number, currency, line items, subtotal, tax and total. Invoice layouts differ between suppliers, so templates or regexes don't scale; a model reads them well, but its output can be wrong, malformed or manipulated by text on the invoice.

## Decision

- **One `ILlmClient` interface**, implemented by `OpenAiCompatibleLlmClient` (`POST {Ai:BaseUrl}chat/completions`). In practice the base URL is my LLM Gateway, which owns retries, provider fallback, caching and budgets, so this client makes **one attempt** and turns every failure (unreachable, timeout, non-2xx, odd body) into `LlmUnavailableException`. With no `Ai:BaseUrl`, a `DisabledLlmClient` is used and `GET /api/ai/status` reports `enabled: false`.
- **PDFs are sent as text, images as images.** PdfPig pulls the text layer out of the PDF, grouped into lines in reading order (at most 10 pages, 20,000 characters). Text is cheaper and more accurate than vision. A PDF without a text layer (a scan) fails with a clear message instead of an empty extraction. PNG and JPEG go to a vision model as a data URL.
- **The prompt treats the invoice as data.** The system prompt says to ignore instructions inside the document (approve, pay, change bank details), and the invoice text is wrapped in explicit markers. This doesn't make injection impossible; the real defence comes later, where no decision that moves money depends on the model alone.
- **JSON mode, parsed defensively** (`ExtractionParser`): take the text between the first `{` and the last `}` (handles code fences and chatter), accept numbers written as `"₹ 1,23,456.50"` or `"(12.00)"`, read tax rates of 18, "18%" or 0.18 as 0.18, accept a few date formats, and turn anything unreadable into **null, never a guess**. Lines are capped at 200.
- **One repair attempt:** if the reply has no usable JSON, the model is shown its reply and the parse error and asked again. Both attempts' token usage is recorded.
- **Arithmetic checks** (`ExtractionChecks`) run on every extraction: required fields present, quantity × price = line amount, lines = subtotal, subtotal + tax = total, with a small rounding allowance (0.01 + 0.1%). They produce **issues** (code + message), not failures: a badly added-up invoice is exactly what the agent must handle.
- **Reading happens in the background.** Upload returns at once; the id goes onto an in-process `Channel`, and `ExtractionWorker` (2 readers by default) moves the invoice `received → extracting → extracted | extractionFailed`. Every step uses the invoice's version check, so a result never overwrites a change someone made meanwhile. On startup, invoices left in `received` or `extracting` (after a crash) are queued again. The worker resolves the extractor in a **new DI scope per invoice**, so the typed `HttpClient` isn't captured by a singleton.
- `POST /api/invoices/{id}/extract` reads an invoice again (after a failure, or once AI is configured). It returns 409 while the invoice is already queued.
- What was read is stored with the invoice: the data, the issues, the source (`pdf-text` / `image`), the model, the number of attempts, token usage and the time.

## Consequences

- CI needs no API key: tests plug in a scripted `FakeLlmClient`, and test PDFs are generated with PdfPig's writer.
- The queue is in memory. With several API instances, or for durability, it would move to a real queue, or to a "claim the oldest received invoice" loop on the database (the version check already makes claiming safe).
- Scanned PDFs need converting to an image first. Rendering PDF pages to images is a later improvement.
