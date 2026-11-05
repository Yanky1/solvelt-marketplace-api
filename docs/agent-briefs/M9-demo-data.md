# M9: test-mode demo data for buyers (API and web)

Owner request, confirmed by Ronald in the orchestrator session on 2026-11-05: "i want the demo-data button the seller portal passed on as your request." This brief is an approved assignment (see CLAUDE.md). It is assigned to the M2 session after its current bug fixes.

## Goal
While the marketplace is in testing, a buyer who has just created an account can press **Fill with demo data** and get a realistic account to click through. **Remove demo data** takes it away again. A test-only **Verify this account** button skips the email verification step.

## Rules (match the seller portal's version)
1. **Config flag `Demo:Enabled`, default false**, in the API and the web.
   - The API and the web each refuse to start when it is true in Production. There is no override, and both refusals are tested.
2. **Public config.** The API's public config exposes `demoEnabled`. When it is true, the web shows a "TEST MODE" badge on every page.
3. **When disabled,** every demo endpoint answers 404, and the web hides the buttons.
4. **Access.**
   - Only the signed-in owner of the account can use the buttons. Take the account from the session, never from the request, so it is IDOR-safe.
   - The endpoints are CSRF-protected on the web, rate-limited, and audited (log who did it, when, and the batch id).
5. **Marking.**
   - Every created row carries `IsDemo = true` and a `DemoBatchId`.
   - **Remove demo data** has a confirm dialog and deletes only rows with `IsDemo` for that account. It never touches real rows. Test this with a mixed account.
6. **Nothing real leaves the system.**
   - No emails, SMS or push.
   - No Stripe calls. Demo orders are created already paid, with fake ids that are never sent to Stripe.
   - No portal calls. Demo orders and bookings are never ingested into, or reconciled with, the portal, and outbox handlers skip demo rows. Test that the outbox stays empty for demo rows.
7. **Account deletion and erasure** also remove demo rows. The PiiRegistry gate must pass.

## Demo data (one press creates, idempotent per account; a second press does nothing while a batch exists)
- A saved address: an example.com email and a 555 phone number.
- A cart with 2 to 3 items from the live catalog. If the catalog is empty, use none and say so.
- 2 to 3 past orders in different states (delivered, shipped, refunded), with tracking.
- 1 to 2 bookings (one upcoming, one completed).
- A message thread with a seller, with a couple of messages.
- Notifications.
- Favourites only if wishlists (M6) exist by then.
- Prefer listings marked `demo: true` in the portal feed (portal CATALOG.md, worker O).

## Also
- **Demo pill.** Show a discreet "Demo" pill on listings whose feed document has `demo: true`.
- **Verify this account.** This test-only button marks the email as verified. It works only when `Demo:Enabled` is true, and only for the signed-in owner.
- **Tests.**
  - API unit and integration tests for every rule above.
  - Web E2E (desktop and mobile) for fill, browse, remove and verify, with axe.
- **Record decisions** in docs/DECISIONS.md (API) and docs/DESIGN.md (web).
- **Merge** under the standing rules, one merge at a time per repo. Ask the orchestrator for your slot.
