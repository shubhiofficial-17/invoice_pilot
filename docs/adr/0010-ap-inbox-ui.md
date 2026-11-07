# 0010: The AP Inbox UI

**Status:** accepted

## Context

The clerk and the approver need one place to see an invoice next to everything the system concluded about it, and to act on it, without trusting anything blindly.

## Decision

- **Inbox** (`/`): every invoice with supplier, invoice number, total, status and issue count. Filter chips: *Needs a person* (awaiting review or approval, or a failed step), *In progress*, *Done*, each with a count. The list **refreshes every 3 s only while something is in progress**, then stops.
- **Invoice page** (`/invoices/:id`), two columns:
  - **left:** the original document (PDF in an iframe, or the image), sticky while scrolling. The iframe URL is built by the app for our own origin, so trusting it with `bypassSecurityTrustResourceUrl` is safe. nginx allows framing of `/api/` responses by the same origin only (`frame-ancestors 'self'`); the app's pages still can't be framed by anyone.
  - **right:** the **recommendation** (outcome, and whether it came from the agent, a policy override, or the policy alone), the reason, the approver, and the supplier email preview. Then **3-way match findings** coloured by severity, **what the invoice says** (lines, tax, total), **the agent's trace** (tool calls with arguments and outputs, collapsed by default, with model calls and tokens), and **the audit trail** with a "chain verified" or "broken at #n" badge.
- **Actions mirror the server rules**, so the UI never offers something the API would refuse:
  - reviewer: *Confirm: <outcome>* or *Change…*. The outcome list disables everything looser than the policy requires, and saving needs a note of at least 5 characters,
  - approver (`awaitingApproval`): *Approve*, or *Reject* (needs a note),
  - every action sends the `expectedVersion` the person was looking at. A 409 or 422 problem is shown as the server's message.
  - The server stays the authority. These are conveniences, not controls.
- The page refreshes every 2 s while the pipeline is still working on the invoice.
- Shared styles (cards, badges, buttons, tables) live in the global stylesheet to keep component styles small.
- Angular 18 signals (`signal`, `computed`, `input.required` bound from the route) and `OnPush` throughout.

## Consequences

- The whole flow can be demonstrated in the browser: upload → watch it read and investigate → review the reasoning against the document → confirm or override → see the audit trail grow.
