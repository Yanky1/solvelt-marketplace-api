# Buyer-marketplace workstreams

| Workstream | Repo | Branch | Scope | Brief | Session | Status | Updated |
|---|---|---|---|---|---|---|---|
| Orchestrator | both | n/a | Plans and supervises M1–M7 | agent-briefs/ORCHESTRATOR.md | session_018hZb8pE5JAKd7eu3Enzfnr | Running | 2026-11-04 |
| M1 Foundation | api | feat/m1-* | Buyer auth/accounts, signed portal client, webhook receiver, outbox, catalog read model | agent-briefs/M1-foundation.md | M1 worker (see session link in orchestrator) | In progress: slice 1 skeleton+CI on feat/m1-foundation | 2026-11-04 |
| W1 Web foundation | web | feat/w1-* | RR7 framework-mode SSR skeleton, Solvelt design tokens, auth pages via BFF, CI + Playwright/axe | (after M1 skeleton) | - | Planned | 2026-11-04 |
| M2 Catalog & search | api+web | - | Categories, brands, filters, full-text search, PDP/service pages, SEO | - | - | Planned | - |
| M3 Cart & checkout | api+web | - | Multi-seller cart, shipping rates, local delivery, tax, Stripe test mode, order ingest | - | - | Planned | - |
| M4 Services | api+web | - | Availability, holds, bookings, quotes | - | - | Planned | - |
| M5 Account area | api+web | - | Orders, bookings, tracking, cancel/returns/refunds, messaging relay, notifications | - | - | Planned | - |
| M6 Reviews & lists | api+web | - | Reviews/ratings, wishlists, recently viewed, moderation hooks | - | - | Planned | - |
| M7 Staff tools | api | - | Decide whether needed beyond portal admin | - | - | To decide | - |

Contract change requests to the seller portal: see docs/CONTRACT.md §9.
