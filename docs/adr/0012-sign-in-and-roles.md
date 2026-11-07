# 0012: Sign-in, roles and segregation of duties

**Status:** accepted

## Context

Approving supplier payments is a controlled activity. Until now every API call was anonymous and every action was recorded as "clerk". A finance system needs to know who acted, restrict who may do what, and stop one person from both processing and approving the same exception.

## Decision

- **Cookie sign-in** (`/api/account/signin`, `/signout`, `/me`): an `ip_auth` cookie that is **HttpOnly** (scripts can't read it, so XSS can't steal it) and **SameSite=Lax** (other sites can't make the browser send it with a POST, which blocks CSRF), with an 8-hour sliding expiry. The app and API share one origin through nginx, so there are no tokens in JavaScript and no refresh logic. The API answers 401/403 and never redirects.
- **Every endpoint requires a signed-in user** (an authorization fallback policy), except `/api/health` and the sign-in calls. `Auth:Enabled=false` turns this off for local experiments and for most tests. The check reads the option at request time, so a test host can turn it back on.
- **Demo users** stand in for the company directory: an AP clerk, two buyers, the AP manager, the finance controller and an admin. They share `Auth:DemoPassword`, hashed with **PBKDF2-SHA256 (210,000 iterations, random salt)** and compared in constant time. Unknown emails still pay for a hash check, so timing doesn't reveal accounts. A real deployment would use the company's identity provider and map its groups to these roles.
- **Who may act** (`ReviewService`, checked on the server for every action):
  - reviewing (`confirm` / `override` on `awaitingReview` or `investigationFailed`): the AP clerk role, or admin,
  - approving (`approve` / `reject` on `awaitingApproval`): **only the approver named in the decision** (the PO's buyer, the AP manager or the controller, chosen by the policy), or admin,
  - **segregation of duties:** nobody who already acted on an invoice can approve it, *including admins*.
- **Real names everywhere:** uploads, human actions and audit events now carry the signed-in email.
- **Rate limits:** uploads (each costs model calls) 30 per minute per user; sign-in 10 per minute per client address, against password guessing. Behind nginx, `ForwardedHeaders:Enabled` makes the API see real client addresses. It's only enabled when the API is reachable solely through the proxy, otherwise clients could fake their address.
- **Cookie keys persist** (`DataProtection:KeysPath` on a Docker volume owned by the non-root user), so a restart doesn't sign everyone out.
- **Web:** a sign-in page, a route guard that asks `/me` once, and an interceptor that treats any API 401 as "session over". The `returnUrl` must be an in-app path (`/…`, not `//…`) to prevent open redirects. The header shows the user and a sign-out link.

## Consequences

- Most tests run with sign-in off (a module initializer sets `Auth__Enabled=false`). `AuthTests` turn it on and cover the full clerk → buyer flow over HTTP with cookies.
- Removing a user's access takes effect when their cookie expires. A real identity provider would add revocation.
