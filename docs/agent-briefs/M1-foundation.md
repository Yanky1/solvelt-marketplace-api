# Brief M1: marketplace API foundation

**Repo:** Yanky1/solvelt-marketplace-api (this repo). **Branch:** `feat/m1-foundation` (later slices: `feat/m1-<slice>`). **Approved assignment** from the
repository owner (see CLAUDE.md): carry it out without asking for confirmation. Record every decision in `docs/DECISIONS.md` (ids `M1-01`, `M1-02`, …).

## Context
Solvelt is a multi-seller marketplace (products and services, kosher-aware). The seller portal (Yanky1/Yanky1-test-login-backend, ASP.NET Core 10,
**read-only for you**) is built; it expects a marketplace on the other end of a signed internal API. You build that marketplace's backend foundation.
The buyer website (Yanky1/solvelt-marketplace-web, React Router v7 framework mode, SSR) is a separate workstream that will call this API from its server.

Read first: `CLAUDE.md`, `docs/CONTRACT.md` (how we consume/produce every portal contract — **follow it exactly**), `docs/AGENTS.md`.
You may attach the portal backend with `add_repo` (owner `Yanky1`, repo `Yanky1-test-login-backend`, access `read`) to read `docs/ORDERS.md` §3,
`docs/PRODUCTS.md`, `docs/SECURITY.md`, `docs/DECISIONS.md` and its code for engineering conventions (module layout, `InboundVerifier`, outbox,
tests). Copy patterns, never modify that repo. If attaching needs owner approval and it doesn't come within a few minutes, carry on from CONTRACT.md.

## Stack and standards (same as the portal backend)
* .NET 10, ASP.NET Core minimal APIs, EF Core; **PostgreSQL** in production (Npgsql), **SQLite** for dev and tests; one solution `Solvelt.Marketplace.slnx`,
  `src/Solvelt.Marketplace.Api`, `tests/Solvelt.Marketplace.Api.Tests` (xUnit, `WebApplicationFactory`, real SQLite).
* GitHub Actions `ci.yml`: restore, build with warnings as errors, test, plus a job that runs the tests against PostgreSQL (service container).
  The workflow must run on pushes to feature branches and on `main`.
* Money `long` cents. Times UTC `DateTimeOffset` from an injected `TimeProvider` (tests control time). Optimistic concurrency via an int `Version`.
* Problem responses `{code, message}`; other users' resources are **404**, never 403. Rate limits on auth and public endpoints.
* No secrets in the repo; config from env; start-up fails in Production when a required secret is missing. Dev/Testing use fixed test values.
* Request logging never logs bodies, tokens, emails or addresses.

## Scope
### 1. Buyer auth and accounts
* Email + password sign-up (strong hashing via ASP.NET Core Identity's `PasswordHasher` or Argon2id), email verification, password reset; generic
  responses so accounts can't be enumerated; lockout/rate limits.
* **Passkeys** (WebAuthn) as sign-in and as a second sign-in method: register, list, rename, delete; use a permissively licensed library (e.g. Fido2NetLib, MIT) and record the choice.
* **Sessions for an SSR front end:** opaque random session tokens, stored **hashed**, sliding + absolute expiry, revocable (sign out, sign out everywhere,
  password change revokes others). The web server holds the token in its own HttpOnly cookie and sends `Authorization: Bearer`. Document this in DECISIONS and in `docs/API.md`.
* **Guest checkout:** a guest buyer record keyed by email with a signed, expiring order-access link; a guest can later claim orders by verifying that email.
* Account: profile (name, phone optional), **addresses** (CRUD, default shipping, US + international fields, optional lat/lng), delete account
  (soft-delete + PII redaction job; orders keep what accounting needs).
* **Relay identity:** each buyer gets a random relay email `relay-<random>@<Marketplace:RelayDomain>` and a display name ("Jane B."); this is what the portal sees.
* Emails go through `IEmailSender` with a dev/test sink (no real provider yet).

### 2. Signed portal client (`SignedPortalClient`)
Exactly CONTRACT.md §1.1: canonical string, per-attempt nonce, exact body bytes, the **portal test vector as a unit test**, deadlines, the retry
policy (what retries and what doesn't), typed methods for `POST /orders`, the buyer events, `GET /shipping/settings`, `POST /local-delivery/eligibility`,
`GET /services/{id}/availability` and `POST /payment-events` (the callers come in later workstreams; give each a typed request/response and a contract test).
Add a **fake portal** for tests (an in-process handler that verifies our signatures with the portal's rules: stale, replay, body hash) so every test exercises real signing.

### 3. Webhook receiver (`POST /api/portal/webhooks`)
Exactly CONTRACT.md §1.2: verify signature over `"<timestamp>.<raw body>"` before parsing, 300 s window, 256 KB cap, idempotent on
`X-Solvelt-Event-Id` in the same transaction as the projection change, order-independent (per-aggregate last-applied timestamps, terminal states never
reopened, **parked** events for unknown aggregates re-applied later), unknown types stored and acked, failures answer 5xx. Write a projection
framework with handlers registered per event type; M1 implements the catalog handlers and a generic store; later workstreams add order/booking handlers.
Our own **transactional outbox** (for calls to the portal and for emails) with leases, exponential backoff, dead-letter and an admin retry endpoint.

### 4. Catalog read model
The portal has **no catalog feed yet** (CONTRACT.md §2, request C1). Build:
* the read model: listings (product or service), variants, prices, availability flag, categories (tree), brands, sellers (display data only), attributes, images (portal image id + dimensions), FAQs;
* `ICatalogSource` with a **fixture implementation** (a realistic seed of ~40 listings across several sellers/brands/categories incl. kosher food, services) and an HTTP implementation written against the C1 shape in CONTRACT.md, marked provisional;
* a sync job: cursor-based poll + `listing.upserted`/`listing.unlisted` events, idempotent and order-independent by `version`; the visibility rule of CONTRACT.md §2;
* public read endpoints the website will use: `GET /api/v1/catalog/categories`, `/brands`, `/listings` (paged, filter by category/brand/seller/type, basic sort), `/listings/{slug-or-id}`. Full-text search and SEO are M2.

### 5. Docs
`README.md` (run, test, config), `docs/API.md` (every endpoint), `docs/DECISIONS.md`, and a **PII table** in DECISIONS: every stored buyer field, why, retention, how it is redacted.

## Review hotspots (the orchestrator checks these on every merge)
Client-controlled time values (never trust a client `deliveredAt`/timestamps); matching across accounts by external ids (match on our ids + portal ids, never tracking numbers/SKUs alone);
idempotency under **parallel** requests (write a test that fires the same webhook/sign-up concurrently); PII retention and redaction of every new field; **signing keys never reused across purposes**
(portal inbound, webhook, session, email link, guest link each have their own key); tokens stored hashed.

## Definition of done
* Feature branch rebased on origin/main, CI green on that exact commit, no test skipped or weakened; merge by fast-forward push (or rebase-merge PR if push to main is refused). Small, reviewable slices are welcome (e.g. skeleton+CI → portal client+receiver → auth → catalog).
* Every regression-relevant behaviour above has a test that would fail without the code.
* Update the M1 row in `docs/AGENTS.md` (status, branch, date) with each merge.

## Working rules
* Never end your turn to ask a question. Decide, record the decision, continue.
* Schedule your own `send_later` check-ins (every ~30 min) so you keep moving while CI runs.
* Don't touch the seller-portal repos (read-only) or any other repository. Contract changes go to the orchestrator, who asks the portal.
* After each merge, post a short summary: what merged (commit sha), tests added, what's next, anything blocked.
