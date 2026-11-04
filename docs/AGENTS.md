# Buyer-marketplace workstreams

| Workstream | Repo | Branch | Scope | Brief | Session | Status | Updated |
|---|---|---|---|---|---|---|---|
| Orchestrator | both | n/a | Plans and supervises M1–M7 | agent-briefs/ORCHESTRATOR.md | session_018hZb8pE5JAKd7eu3Enzfnr | Running | 2026-11-04 |
| M1 Foundation | api | feat/m1-* | Buyer auth/accounts, signed portal client, webhook receiver, outbox, catalog read model | agent-briefs/M1-foundation.md | session_018hZb8rdQJTwYBEtUV4GvDf | In progress: slices 1, 2a, 2b-i, reviews 1-4 and the catalog merged (2994b97); review 5 merged (64cba53); outbox merged (af8d339); auth core (sign-up, verify, sign-in, sessions, reset, relay identity) on feat/m1-auth-2; next: review 6 items, account, passkeys, guest. Open item (M2-02): the initial EF migration must also run SearchSchema.PostgresDdl and CREATE EXTENSION IF NOT EXISTS pg_trgm (the DB role needs permission or an operator pre-creates it) | 2026-11-04 |
| W1 Web foundation | web | feat/w1-* | RR7 framework-mode SSR skeleton, Solvelt design tokens, BFF auth (sign-in/up, verify, reset, profile, addresses, passkeys), CI + Playwright/axe. Catalog pages moved to M2 (W1-53). | agent-briefs/W1-web-foundation.md | session_018hZb8rg4HdxcFLSY4vTN1q | Done pending orchestrator verification of a8f659b | 2026-11-04 |
| M2 Catalog & search | api+web | feat/m2-* | Facets, full-text search, autocomplete, PDP/service pages, SEO | agent-briefs/M2-catalog-search.md | session_018hZb8pzncReTZQrqqLDBum | In progress: API slice 1 merged (445ed53: search, suggest); slices 1-3 merged (3768a5c); catalog hardening (reviews B-1/B-2, 3-11, search review incl. Unicode) on feat/m2-catalog-hardening; next web | 2026-11-04 |
| M3 Cart & checkout | api+web | feat/m3-* | Multi-seller cart, shipping, local delivery, tax, Stripe test mode, ingest, refunds/disputes | agent-briefs/M3-cart-checkout.md | session_018hZb8qprqkj4nfuTti5DS6 | In progress: API slice 1 merged (86a63bf: cart, grouping, merge); slice 2 merged (4672b95: shipping, local delivery, tax, quote); slice 3 merged (ad010c2: orders, Stripe provider + fake, checkout, key rules); slice 4 merged (467784e: Stripe webhook, order ingest, payment.captured, refund cap, expiry); slice 5 (portal refund/cancel events, tax-aware refunds, Stripe external refunds, disputes) on feat/m3-refunds-5; next web slices (waiting for the orchestrator to say M2's PDP merged) | 2026-11-04 |
| M4 Services | api+web | feat/m4-* | Availability, holds, bookings, quotes, fees | agent-briefs/M4-services.md | - | Queued (gate G4) | 2026-11-04 |
| M5 Account area | api+web | feat/m5-* | Orders/bookings, tracking, after-sales, messaging relay, notifications | agent-briefs/M5-account-area.md | - | Queued (gate G5) | 2026-11-04 |
| M6 Reviews & lists | api+web | feat/m6-* | Reviews, wishlists, recently viewed, moderation hooks | agent-briefs/M6-reviews-lists.md | - | Queued (gate G6) | 2026-11-04 |
| M7 Staff tools | api+web | feat/m7-* | Minimal marketplace staff area (O-06) | agent-briefs/M7-staff-tools.md | - | Queued (gate G7) | 2026-11-04 |
| M8 Journey E2E | api+web | feat/m8-* | Real-portal buyer journey E2E, screenshots | agent-briefs/M8-journey-e2e.md | - | Queued (gate G8) | 2026-11-04 |

**Ownership (2026-11-04 08:00):** M2 owns the catalog read model and sync plus all catalog, search and listing pages in the web repo. W1 owns the shell, the design system and auth/account. M1 owns auth/accounts, the portal client, the receiver and the outbox.

**Open items across workstreams**
- PostgreSQL production schema: the first EF migration must run SearchSchema.PostgresDdl and `CREATE EXTENSION IF NOT EXISTS pg_trgm` (M2-02); the DB role needs the permission, or an operator pre-creates it. Owner: M1. Launch gate.
- Email provider: production start-up fails until one is configured (M1 review of af8d339). Owner action before launch.
- M8 needs a read-only `BACKEND_REPO_TOKEN` secret (the portal backend repo is private). Owner action.
- Public-page caching vs the visitor cookie (W1-23): M2 decides with its first web slice.

Contract change requests to the seller portal: see docs/CONTRACT.md §9.

## Launch gates (the orchestrator starts each worker as soon as its gate is met; owner instruction 2026-11-04: run all workstreams without waiting for him)
Every worker gets COMMON.md + its brief inline, model claude-sonnet-5-5, source_url = API repo, extra_allowed_tools add_repo.
| Gate | Starts | Condition (merged on main) |
|---|---|---|
| G2 | M2 | M1 catalog read model + public catalog endpoints; W1 skeleton slice |
| G3 | M3 | M1 portal client + webhook receiver + outbox + auth/guest; W1 auth slice; M2 PDP slice |
| G4 | M4 | G3's conditions + M3 payment slice (intent + payment.captured) |
| G5 | M5 | M3 order ingest slice |
| G6 | M6 | M2 PDP slice (wishlists/recently viewed); reviews wait for M5 delivered projections |
| G7 | M7 | M3 payments + M5 projections |
| G8 | M8 | M3, M5, M6 merged; portal C1 merged |
At most 4 workers run at once; the next gate waits for a slot.

## Open items for other workstreams (kept here so they are not lost)
| Item | Owner | Detail |
|---|---|---|
| PostgreSQL search schema in the migration | M1 (migration slice) | `InitialCreate` must run `SearchSchema.PostgresDdl` with `migrationBuilder.Sql` (M2-02, M2-13): `pg_trgm`, the function `catalog_tokens_tsvector`, the generated `document` column and both GIN indexes. The database role needs `CREATE EXTENSION`; the database needs a **UTF-8 locale** for typo tolerance in non-Latin text. Launch gate. |
| Portal C1 additions | orchestrator -> portal | optional `searchTerms` per listing (merged on the portal side, shape to be forwarded); delivery as attribute `delivery` (ships/local/at-home/online) now, explicit `delivery` object with C5 later (reader prefers it, M2-15) |
| Seller state ordering | portal (C1) | seller state is ordered by listing snapshot time (M2-15); a per-seller version in the feed would be exact |
