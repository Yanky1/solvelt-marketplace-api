# Buyer-marketplace workstreams

| Workstream | Repo | Branch | Scope | Brief | Session | Status | Updated |
|---|---|---|---|---|---|---|---|
| Orchestrator | both | n/a | Plans and supervises M1–M7 | agent-briefs/ORCHESTRATOR.md | session_018hZb8pE5JAKd7eu3Enzfnr | Running | 2026-11-04 |
| M1 Foundation | api | feat/m1-* | Buyer auth/accounts, signed portal client, webhook receiver, outbox, catalog read model | agent-briefs/M1-foundation.md | session_018hZb8rdQJTwYBEtUV4GvDf | In progress: slices 1, 2a, 2b-i, reviews 1-4 and the catalog merged (2994b97); review 5 merged (64cba53); outbox merged (af8d339); auth core merged (09ea829); review 6 (outbox/admin hardening, review-4 follow-ups) and auth hardening (feat/m1-auth-hardening: gate, verify-with-password, step-up, rotate) in CI; guest links merged; independent-review fixes (H1, M2, M3, L4-L8) on feat/m1-review-9 in CI; account slice (profile, addresses, password change, deletion and redaction, no-store on auth/account) rebased on it, next to merge; then passkeys, Unicode normaliser, InitialCreate migration last. Open item (M2-02): the initial EF migration must also run SearchSchema.PostgresDdl and CREATE EXTENSION IF NOT EXISTS pg_trgm (the DB role needs permission or an operator pre-creates it); account slice, passkeys (c0fb1bc), schema check (ae1db48), erasure of the inbound event store + NFC normaliser + passkey review follow-ups (d6fb650), InitialCreate EF migration with the baseline of an EnsureCreated database (dc5df4c, M1-84). Remaining: review items from the orchestrator only; any model change now needs the migration updated (README, Database schema (migrations)) | 2026-11-04 |
| W1 Web foundation | web | feat/w1-* | RR7 framework-mode SSR skeleton, Solvelt design tokens, BFF auth (sign-in/up, verify, reset, profile, addresses, passkeys), CI + Playwright/axe. Catalog pages moved to M2 (W1-53). | agent-briefs/W1-web-foundation.md | session_018hZb8rg4HdxcFLSY4vTN1q | Finishing: last slice = verify-email password (O-16) + step-up endpoint | 2026-11-04 |
| M2 Catalog & search | api+web | feat/m2-* | Facets, full-text search, autocomplete, PDP/service pages, SEO | agent-briefs/M2-catalog-search.md | session_018hZb8pzncReTZQrqqLDBum | In progress: API slice 1 merged (445ed53: search, suggest); slices 1-3 merged (3768a5c); catalog hardening (reviews B-1/B-2, 3-11, search review incl. Unicode) on feat/m2-catalog-hardening; next web | 2026-11-04 |
| M3 Cart & checkout | api+web | feat/m3-* | Multi-seller cart, shipping, local delivery, tax, Stripe test mode, ingest, refunds/disputes | agent-briefs/M3-cart-checkout.md | session_018hZb8qprqkj4nfuTti5DS6 | API slices 1-5 merged (86a63bf..a293e8d); fixes in flight: one pending order per cart (critical), refund tax (C9), O-15, ingest review, cart review; web after M2's PDP | 2026-11-04 |
| M4 Services | api+web | feat/m4-* | Availability, holds, bookings, quotes, fees | agent-briefs/M4-services.md | session_018hZb8qnyz86ixxQ4TvoC7C | API merged: availability (eb27be3), holds + booking.* receiver + booking payments + buyer actions (0c24759), booking-money review fixes (a597e57); next: availability review fixes, service options, .ics, quotes, N8 quote holds, then web (needs M2's service page) | 2026-11-04 |
| M5 Account area | api+web | feat/m5-* | Orders/bookings, tracking, after-sales, messaging relay, notifications | agent-briefs/M5-account-area.md | session_018hZb8rkK2514h97PhkHGYf | Merged on main: slice 1 (a33ca41), slices 2-3 + review-1 (5ec31a8), slice 4 messaging relay + review P1-P4 (ca6c635), review P5/P7/P6v1 (820b369); web main 8e3950a has orders, tracking, returns, notifications, /unsubscribe, messages. Queued: API review-4a (O-21 full) paired with web-4a, web-5a (review W2/W3/L). Next: delete notification prefs with the account (M1 IAccountEraser), guest-link order view (after M3), booking messages, privacy pages (download my data, delete account) | 2026-11-04 |
| M6 Reviews & lists | api+web | feat/m6-* | Reviews, wishlists, recently viewed, moderation hooks | agent-briefs/M6-reviews-lists.md | - | Queued (gate G6) | 2026-11-04 |
| M7 Staff tools | api+web | feat/m7-* | Minimal marketplace staff area (O-06) | agent-briefs/M7-staff-tools.md | - | Queued (gate G7) | 2026-11-04 |
| M8 Journey E2E | api+web | feat/m8-* | Real-portal buyer journey E2E, screenshots | agent-briefs/M8-journey-e2e.md | - | Queued (gate G8) | 2026-11-04 |

**Ownership (2026-11-04 08:00):** M2 owns the catalog read model and sync plus all catalog, search and listing pages in the web repo. W1 owns the shell, the design system and auth/account. M1 owns auth/accounts, the portal client, the receiver and the outbox.

**Open items across workstreams**
- PostgreSQL production schema: the first EF migration must run SearchSchema.PostgresDdl and `CREATE EXTENSION IF NOT EXISTS pg_trgm` (M2-02); the DB role needs the permission, or an operator pre-creates it. Owner: M1. Launch gate. **Done in InitialCreate (M1-84): `InitialCreate.SearchSchema.cs`; from now on every model change needs the migration regenerated (see README, Database schema).**
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
| Variant-less listings in orders | M3 | A listing the portal sends with no variants is stored with one synthetic variant (`catalog_variants.IsDefault = true`, id = listing id; M2-18). Order ingest must send `productId` with `variantId: null` for it, never the listing id as a variant id (portal docs/ORDERS.md section 3). The migration must include the `IsDefault` column. |
| Shipping weight from the catalog | M3 | `CatalogVariant.WeightGrams` (int?, null = unknown; M2-24) replaces parsing the `weight` attribute when present. The migration must include `catalog_variants.WeightGrams` and `catalog_listings.LengthMm/WidthMm/HeightMm` (all nullable int). |
| Demo data columns | M1 | The migration must include, on `buyer_addresses`, `carts`, `marketplace_orders`, `bookings`, `message_threads` and `notifications`: `IsDemo` (bool, default false, not null) and `DemoBatchId` (uuid null); and `catalog_listings.IsDemoListing` (bool, default false); and, on `cart_lines`, `IsDemo` (bool, default false, not null; M9-05). No new table. The PII gate lists a `demo-data` module (eraser registered first). M9-02. |
| PostgreSQL search schema in the migration | M1 (migration slice) | `InitialCreate` must run `SearchSchema.PostgresDdl` with `migrationBuilder.Sql` (M2-02, M2-13): `pg_trgm`, the function `catalog_tokens_tsvector`, the generated `document` column and both GIN indexes. The database role needs `CREATE EXTENSION`; the database needs a **UTF-8 locale** for typo tolerance in non-Latin text. Launch gate. |
| Demo data columns | M1 | The migration must include, on `buyer_addresses`, `carts`, `marketplace_orders`, `bookings`, `message_threads` and `notifications`: `IsDemo` (bool, default false, not null) and `DemoBatchId` (uuid null); and `catalog_listings.IsDemoListing` (bool, default false). No new table. The PII gate lists a `demo-data` module (eraser registered first). M9-02. |
| PostgreSQL search schema in the migration | M1 (migration slice) | `InitialCreate` must run `SearchSchema.PostgresDdl` with `migrationBuilder.Sql` (M2-02, M2-13): `pg_trgm`, the function `catalog_tokens_tsvector`, the generated `document` column and both GIN indexes. The database role needs `CREATE EXTENSION`; the database needs a **UTF-8 locale** for typo tolerance in non-Latin text. Launch gate: DONE in InitialCreate (M1-84). |
| Portal C1 additions | orchestrator -> portal | optional `searchTerms` per listing (merged on the portal side, shape to be forwarded); delivery as attribute `delivery` (ships/local/at-home/online) now, explicit `delivery` object with C5 later (reader prefers it, M2-15) |
| Seller state ordering | portal (C1) | seller state is ordered by listing snapshot time (M2-15); a per-seller version in the feed would be exact |
