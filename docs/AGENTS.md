# Buyer-marketplace workstreams

| Workstream | Repo | Branch | Scope | Brief | Session | Status | Updated |
|---|---|---|---|---|---|---|---|
| Orchestrator | both | n/a | Plans and supervises M1–M7 | agent-briefs/ORCHESTRATOR.md | session_018hZb8pE5JAKd7eu3Enzfnr | Running | 2026-11-04 |
| M1 Foundation | api | feat/m1-* | Buyer auth/accounts, signed portal client, webhook receiver, outbox, catalog read model | agent-briefs/M1-foundation.md | session_018hZb8rdQJTwYBEtUV4GvDf | In progress: slices 1, 2a, 2b-i and reviews 1-4 merged (02c0e6a); catalog on feat/m1-catalog-r2 (CI); review 5 (log guard, limiter eviction), outbox, auth next | 2026-11-04 |
| W1 Web foundation | web | feat/w1-* | RR7 framework-mode SSR skeleton, Solvelt design tokens, BFF auth pages, basic catalog pages, image resize, CI + Playwright/axe | agent-briefs/W1-web-foundation.md | session_018hZb8rg4HdxcFLSY4vTN1q | Running | 2026-11-04 |
| M2 Catalog & search | api+web | feat/m2-* | Facets, full-text search, autocomplete, PDP/service pages, SEO | agent-briefs/M2-catalog-search.md | - | Queued (gate G2) | 2026-11-04 |
| M3 Cart & checkout | api+web | feat/m3-* | Multi-seller cart, shipping, local delivery, tax, Stripe test mode, ingest, refunds/disputes | agent-briefs/M3-cart-checkout.md | - | Queued (gate G3) | 2026-11-04 |
| M4 Services | api+web | feat/m4-* | Availability, holds, bookings, quotes, fees | agent-briefs/M4-services.md | - | Queued (gate G4) | 2026-11-04 |
| M5 Account area | api+web | feat/m5-* | Orders/bookings, tracking, after-sales, messaging relay, notifications | agent-briefs/M5-account-area.md | - | Queued (gate G5) | 2026-11-04 |
| M6 Reviews & lists | api+web | feat/m6-* | Reviews, wishlists, recently viewed, moderation hooks | agent-briefs/M6-reviews-lists.md | - | Queued (gate G6) | 2026-11-04 |
| M7 Staff tools | api+web | feat/m7-* | Minimal marketplace staff area (O-06) | agent-briefs/M7-staff-tools.md | - | Queued (gate G7) | 2026-11-04 |
| M8 Journey E2E | api+web | feat/m8-* | Real-portal buyer journey E2E, screenshots | agent-briefs/M8-journey-e2e.md | - | Queued (gate G8) | 2026-11-04 |

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
