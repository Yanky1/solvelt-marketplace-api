# Q. Quantity offers, buyer side (seller-portal issue #101, contract C18)

**Approval.** Ronald, the repository owner, approved this directly in the buyer orchestrator's chat on 2026-11-18: "Approved: after the voice fixes merge, build the buyer side of issue Yanky1/Yanky1-test-login-backend#101 (quantity offers: cart pricing, checkout, listing badge, cart message, and the phone assistant quoting the same totals)."

- The voice fixes are merged (API main 071cebc).
- This is the ONE marketplace exception to the hold; touch nothing else.

## What the seller side built (read these first, READ-ONLY)
- `docs/CONTRACT.md`, section C18; `docs/CATALOG.md`, section 11; `docs/ORDERS.md`, section 3.2 (Yanky1/Yanky1-test-login-backend, main: #102 b1a2c09, frontend #59 fe7ead0, docs #103; PR #104 adds tests only).

## Owner decisions and defaults (from the portal)
- Tiers: a per-unit price by minimum quantity, counted across ONE seller's eligible products (20 cards cost $18.00; 25 cards cost 25 x $0.90 = $22.50).
- One gift per order, no stacking. A tier never raises a price; a lower sale price wins.
- An out-of-stock gift: the order proceeds without it (giftUnavailable).
- Only the offer's eligible units count: other sellers' and other brands' units never do.

## Sanity figures (must match the portal exactly)
- 25 units at a single $0.90 tier = 2250 cents.
- Tiers 10 -> 95c and 20 -> 90c: 15 units = 1425 cents, 25 units = 2250 cents.

## Build (API slice first, then web; each its own branch and merge)
1. **API:** project offers from the catalog feed (events and tombstones) through the existing projection framework; apply them in CartService and QuoteService/CheckoutService with ONE shared pricing function (cart, quote, checkout, the order sent to the portal and the voice read-back all use it); send offerId/offerVersion, discountCents and gift lines exactly as C18 says; handle 422 `offer_mismatch` (re-quote with the portal's expected figures, never charge a stale total, clear buyer message); money in long cents.
2. **Voice:** the phone assistant's quote and read-back come from the same server quote. Add one scripted voice test with a tiered cart.
3. **Web (solvelt-marketplace-web, push):** listing badge ("Buy 20+, $0.90 each"), cart message ("Add 5 more for $0.90 each" / "Free gift included" / "Gift unavailable"), checkout lines showing the discount and the gift. Follow DESIGN.md, axe in light and dark, screenshots, E2E.

## Tests (each fails when its line is reverted)
Sanity figures; a tier never raises a price; a sale price beats a tier; cross-seller and cross-brand units don't count; one gift per order; giftUnavailable; `offer_mismatch` leads to a re-quote and nothing is charged; tombstoned offers stop applying; parallel cart edits stay consistent; the portal journey gains a tiered order the portal ACCEPTS.

## Merging and records
Standing rules (CLAUDE.md). Decisions as Q-nn in docs/DECISIONS.md (API) and docs/DESIGN.md (web); a "Q" row in docs/AGENTS.md. First commit: this brief.
