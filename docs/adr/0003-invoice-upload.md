# 0003: Invoice upload and storage

**Status:** accepted

## Context

Invoices arrive as PDFs (some generated, some scanned) or as photos. Before the agent can read one, the file has to be accepted safely, stored, and shown back to the AP clerk next to whatever the agent concludes.

## Decision

- **`POST /api/invoices`** takes one multipart file up to **10 MB**. Missing or oversized files get a 400 validation problem.
- **The file type comes from the bytes** (`%PDF-`, the PNG signature, the JPEG `FF D8 FF` marker), never from the browser's Content-Type or the file name. Anything else gets **415**. Downloads are served with that sniffed type, `Content-Disposition: inline` and `X-Content-Type-Options: nosniff`, so an uploaded HTML or script file can never be served back as a page.
- **File names are cleaned:** path parts, control characters and quotes are removed, and the length is capped at 120 characters (keeping the extension).
- **Duplicate uploads are idempotent:** the SHA-256 of the file is stored with a **unique index**. Uploading the same file again returns the existing invoice (200 instead of 201), even when two uploads race. A clerk dragging the same PDF in twice shouldn't create two inbox items. Catching a *business* duplicate (the same invoice sent as a different file) is the matcher's job, not this one.
- **Invoice ids** are `ap_` plus 12 characters from an alphabet without look-alikes, so they don't get confused with the supplier's own invoice number.
- **Storage:** metadata in `invoices`, file bytes in a separate `invoiceFiles` collection (one document per invoice). The inbox list never loads files, and 10 MB is well under MongoDB's 16 MB document limit, so GridFS isn't needed.
- **Optimistic concurrency:** each invoice has a `Version`. `TryUpdateAsync` writes only if the version is unchanged and returns `null` otherwise. The extraction worker (next milestone) and people acting in the UI will update the same invoice.
- Timestamps are kept to the millisecond, which is what MongoDB stores, so values round-trip exactly.
- Antiforgery tokens are disabled on the upload endpoint. The API is same-origin behind nginx with no cookie auth yet; when auth arrives (milestone 11), the SameSite cookie is the CSRF defence.

## Consequences

- An invoice starts as `received`. Milestone 3b adds the background extraction that moves it to `extracted` or `extractionFailed`.
- The web inbox lists invoices, uploads files and links to the original document.
