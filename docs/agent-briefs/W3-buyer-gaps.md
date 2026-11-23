# W3: close the buyer-side gaps (worker brief)

Ronald (the owner) decided on 2026-11-23 that the buyer side must "work completely" for a real buyer, end to end. Payments use test keys, which the owner supplies. This brief is the approved assignment. The buyer-marketplace orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) reviews every sha before you merge it. Send the sha with `send_message` before you push.

## Scope

You work in both repositories:
- the API: Yanky1/solvelt-marketplace-api;
- the website: Yanky1/solvelt-marketplace-web.

Attach the website repo with add_repo (owner "Yanky1", access "push"). Worker V (session_018hZb8ngQmPwaesx1JGPzjh) owns everything voice and phone (`src/**/Voice/**`, PayConnector, Sola), so do not touch those. If a task needs a voice change, tell the orchestrator.

Work through the items in this order. Each item is its own branch, gets its own DECISIONS.md row (API) or DESIGN.md entry (web), and merges separately.

1. **List services priced by quote.** These are quarantined today as `quote_price` (`HttpCatalogSource.cs:125`), and the fix is proposed in M6-11. List them with a "Request a quote" call to action instead of a price, using the quote flow that already exists (M4). Search, filters and sorting must treat them sensibly: no price sort position, and no "from $0". Add tests on both sides.
2. **Give phone-created accounts a web path** (V-05 and V-09 follow-ups). A buyer whose account was created by phone has no email and no password. They need to be able to:
   - sign in on the web by verifying their phone with an SMS code, using the existing PhoneCodeService and SignalWire settings, with the same attempt limits;
   - then add a verified email, and a password or passkey.

   Check what forgot-password does for an account that has never had a password, and fix it if it fails or leaks whether an account exists. Add API endpoints, web routes, e2e tests and axe checks.
3. **Drive the navigation from the categories.** `Shell.tsx:11` is a fixed "Placeholder" list. Build the navigation from the catalog categories (top level, ordered as the portal orders them), with SSR and caching. Add e2e tests.
4. **Docs and operations:**
   - fix the stale docs: the web README says the sitemap is a placeholder (it is real), and the API README config table has no `Stripe__*` rows;
   - write `docs/DEPLOY.md` in the API repo, covering both apps: every required and optional setting, with its secret or not-secret status, length rules and "must differ" rules; the Stripe dashboard webhook URL and its events; the portal webhook URL; the reverse proxy and how `/api` sits next to the web origin; TLS; the Postgres requirements (`pg_trgm`, a UTF-8 locale); and the first staff admin. Mark anything unknown as "owner to decide" rather than guessing.
5. **Make the real-Stripe CI job runnable.** It is skipped because `STRIPE_TEST_SECRET_KEY` is unset. Document exactly which repository secrets the owner must add (the secret and a `pk_test_` key for 3DS). Make sure that, once they are set, the job covers the 3DS test card and webhook delivery: the M8-09 follow-ups, with the webhook delivered through the Stripe CLI or a signed replay. Never commit a key.

Out of scope: tax rates (owner review), the weight-based shipping contract (C7, portal), seller payouts, and live keys.

## Rules

Read CLAUDE.md in both repos; it binds you. In short:
- work on a feature branch, rebased on origin/main;
- merge only when every workflow is green on the exact sha (CI and Journey for the API; CI and E2E for the web), by fast-forward or rebase-merge;
- never rewrite a pushed branch; push a new name instead;
- never skip, delete or weaken a test;
- never commit secrets, and never use live keys;
- never change seller-portal code, and touch no other repository;
- leave model ids out of commits;
- if a vendor or doc host is blocked by the network, ask the orchestrator for the facts. Do not write them from memory.
