# Marketplace ⇄ seller-portal contract (consumer side)

The seller portal (Yanky1/Yanky1-test-login-backend) owns the contract. This file says how the marketplace API **consumes** each portal
endpoint and **produces** what the portal expects. Source of truth, pinned at portal commit `6fb3493`:
`docs/ORDERS.md` §3, `docs/SERVICES.md` §6, `docs/PAYMENTS.md` §6, `docs/SHIPPING.md` §4–5, `docs/LOCAL-DELIVERY.md` §4, `docs/PRODUCTS.md`, `docs/ADMIN.md` (moderation).
If this file and the portal docs disagree, the portal docs win and this file is fixed. Contract changes are requested from the seller-portal
orchestrator (see §9); we never change portal code.

## 1. Two directions, two secrets

| Direction | Transport | Secret (marketplace config) | Portal config |
|---|---|---|---|
| Marketplace → portal (calls) | `https://<portal>/api/internal/v1/...`, signed request | `Portal:InboundSecret` | `Orders:InboundSecret` |
| Portal → marketplace (outbox webhooks) | `POST <marketplace>/api/portal/webhooks` | `Portal:WebhookSecret` | `Orders:OutboundSecret` |

* Start-up fails outside Development/Testing when either secret is missing or when they are equal. Neither secret is ever reused for anything
  else (buyer sessions, email links, image URLs, CSRF, passkeys each get their own key; see DECISIONS).
* Secrets come only from env (`Portal__InboundSecret`, `Portal__WebhookSecret`). Tests use fixed test values.

### 1.1 Signing our calls (`SignedPortalClient`)
Headers `X-Solvelt-Timestamp` (unix seconds, our clock), `X-Solvelt-Nonce` (32 random hex, new per **attempt**, including retries),
`X-Solvelt-Signature: v1=<hex HMAC-SHA256(secret, canonical)>`, where canonical = `timestamp\nnonce\nMETHOD\n<encoded path+query exactly as sent>\n<hex sha256(body bytes)>`.
GET bodies are empty. The client serialises the body once and signs/sends **those exact bytes**. Must pass the portal test vector:
secret `test-secret`, ts `1700000000`, nonce `abc123`, `POST /api/internal/v1/orders`, body `{"a":1}` →
body hash `015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862`, signature `v1=8a1a05a14201d38431d0c9816b6b127ff9193117e81e0530895a25934cbaadea`.

Client behaviour: request deadline ≤ 30 s (portal asks for well under 2 min); retry with backoff on network errors, `503` (honour `Retry-After`),
`409 concurrent_update`/`version_conflict` where the portal says "re-send", and the retryable `404 order_not_found | booking_not_found` /
`409 sale_not_recorded` of payment events. Never retry `401`, `413`, `422`. Every write carries our own idempotency id (below), so retries are safe.
Bodies stay under 256 KB.

### 1.2 Receiving webhooks (`PortalWebhookReceiver`)
`POST /api/portal/webhooks`, anonymous, body cap 256 KB. Verify **before parsing**: `X-Solvelt-Signature == v1=HMAC(Portal:WebhookSecret, "<X-Solvelt-Timestamp>.<raw body>")`
(constant-time), timestamp within 300 s. Then:
1. **Idempotent:** insert `X-Solvelt-Event-Id` into `portal_inbound_events` (unique). A duplicate returns `200` and does nothing.
   The insert and the read-model change commit in **one transaction**; a failure rolls both back and answers `5xx` so the portal retries.
2. **Replay-safe:** the event id table is the replay guard (an old signed body re-posted is a duplicate); timestamps outside the window are `401`.
3. **Order-independent:** events can arrive late, twice, or out of order (retries with backoff up to 8 attempts, then dead-letter + manual retry).
   Each projection stores the portal `createdAt` of the last event applied per aggregate and per field group, and an older event never overwrites
   newer state. Terminal states (cancelled, refunded, delivered) are never reopened by an older event. An event for an aggregate we don't know yet
   (e.g. `order.shipped` before our ingest call returned) is stored as **parked** and re-applied when the aggregate appears; it is still acked `2xx`.
4. Unknown event types are stored and acked (forward compatible). Ack fast: heavy work (emails, notifications) goes to our own outbox.

Envelope: `{ id, type, createdAt, data }`; `data.order = {sellerOrderId, sellerOrderNumber, marketplaceOrderId}`, `data.booking = {bookingId, number, marketplaceBookingId}`.
**Matching rule (review hotspot):** an event is matched to our records by the marketplace id we sent (`marketplaceOrderId`, `marketplaceBookingId`,
`marketplaceReturnId`, `requestId`) **and** the portal id it carries, never by tracking number, SKU or any other external id alone.

## 2. Catalog — portal docs/CATALOG.md (C1, merged 73845da); the section below was our proposal, CATALOG.md wins
The portal has no buyer-facing catalog feed yet (ROADMAP: "public read API over Active products", "outbox of product change events, `Version` is the cursor").
Requested from the seller-portal orchestrator (§9, request C1):
* signed `GET /api/internal/v1/catalog/listings?since=<cursor>&limit=` (Active products **and** services, variants, prices, stock availability,
  category, brand, seller display data, attributes, FAQs, published Q&A, image ids + dimensions), `GET /catalog/listings/{id}`, `GET /catalog/categories`, `GET /catalog/brands`;
* outbox events `listing.upserted` / `listing.unlisted` (status leaves Active, seller suspended, archived) carrying `{listingId, version}`;
* image bytes via signed `GET /api/internal/v1/catalog/images/{imageId}` (we resize and serve them from our own CDN path);
* signed `POST /api/internal/v1/products/{id}/questions` replacing `/api/dev/questions`.

Until C1 lands, M1 builds the read model behind `ICatalogSource` with a fixture/fake implementation, and the sync job (poll by cursor + apply events)
against that interface. **Visibility rule:** a listing is shown only while its last known portal state is Active, its seller is active/approved and
it has not been unlisted; an `unlisted` event hides it immediately even if a poll page is stale.

## 3. Orders (ORDERS.md §3)
**We call:**
| Portal endpoint | When | Our idempotency id |
|---|---|---|
| `POST /orders` | after `payment_intent.succeeded` (§6) | `marketplaceOrderId` = our order number `MP-…` |
| `POST /orders/{mid}/cancel-requests` | buyer cancels | `requestId` (ours, stored before the call) |
| `POST /orders/{mid}/return-requests` | buyer return | `returnId` |
| `POST /orders/{mid}/delivery-confirmations` | buyer clicks "received" | (idempotent by state); `deliveredAt` is **server time**, never a client value |
| `POST /messages`, `/messages/{id}/reports`, `/messages/{id}/receipts`, `GET /attachments/{id}` | messaging relay (M5) | `messageId` |

Ingest body: one line per cart line with `marketplaceLineId`, `sellerId`, `brandId`, `productId`(+`variantId`) — never by SKU; `shipping[]` per
(seller, brand) group; `deliveries[]` for local delivery groups; money in cents; the buyer as **display name + relay email** (`relay-<random>@<relay domain>`,
never the real email) + optional phone. `422 invalid_order` means **nothing was created**: we refund the payment intent in full and show the errors.
`201`/`200 created:false` both store the returned seller order ids/numbers.

**We receive:** `order.accepted|held|resumed|shipped|tracking_updated|delivered|cancelled`, `cancellation.decided`, `return.decided|received`,
`refund.issued` (**we move the money**: Stripe refund → then we send `refund.processed`), `message.sent`, plus `shipment.delivered`, `shipment.voided`,
`return.label_created`, `delivery.attempt_failed`, `order.delivery_method_changed`. `order.cancelled.refundDueCents` → we refund that amount.

## 4. Services and bookings (SERVICES.md §6)
**We call:** `GET /services/{id}/availability` (≤ 62 days, cached ≤ 30 s per query; one portal rate bucket per service is shared by all shoppers, so we
cache and coalesce), `POST /bookings/holds` (10-minute hold during checkout), `POST /bookings/{mid}/confirm`, `POST /bookings`, `GET /bookings/{mid}`,
`reschedule-requests`, `cancellations`, `proposal-responses`, `messages`, `POST /quote-requests`, `/quotes/{qid}/accept|decline`. Ids: `marketplaceBookingId` `BK-…`, `requestId`, `messageId`.
**We receive:** every `booking.*` and `quote.*` event and `message.sent` (kind booking). `booking.reminder` → we send the buyer reminder.
`booking.cancelled|no_show.refundDueCents` → refund; `booking.additional_charge_requested` is a **request** to the buyer, charged only after buyer consent.

## 5. Shipping and local delivery (SHIPPING.md §4–5, LOCAL-DELIVERY.md §4)
* Checkout rates: `GET /shipping/settings?sellerId=&brandId=` (cache ≤ 5 min, invalidate on error). We compute the price: flat, or
  `baseCents + ceil(weightGrams/1000) × perKgCents`; free at the threshold on the **items subtotal of that (seller, brand) group**.
* Local delivery: `POST /local-delivery/eligibility` per group; the quote reserves nothing, the window is re-checked on ingest
  (`delivery_window_full` etc. → we refund and ask the buyer to pick again).
* Tracking: `order.tracking_updated` (carrier: `status`, `events`, `eta`; local: `eta.windowStart/End/estimatedAt`, `driverFirstName`, `position` only while
  the buyer's stop is next). **Positions** are shown live and kept at most 24 h, never logged; **proof URLs** are short-lived and proxied, never stored past expiry.
* Return label: `POST /returns/{marketplaceReturnId}/label-link` for a fresh link.

## 6. Payments (PAYMENTS.md §6) — we produce
Stripe on the **platform** account, test mode only, Payment Intents + Elements (3DS/SCA), separate charges and transfers (the portal does transfers).
`POST /payment-events {eventId, type, data}` with our own `eventId` (stored in our outbox before sending):
* `payment.captured` `{kind: order|booking|booking_cancellation_fee|booking_no_show_fee|booking_extra_charge, marketplaceOrderId|marketplaceBookingId, sellerOrderNumber?, paymentRef: pi_…, currency, amountCents?, capturedAt}` — sent **after** order ingest returned the seller orders (`404 order_not_found` is retried).
* `refund.processed` `{refundId (re_…), marketplaceOrderId + sellerOrderNumber | marketplaceBookingId, amountCents (items), shippingCents, currency, reason}` — per seller order; `422 exceeds_sale` is a bug alarm, not retried.
* `dispute.created|updated|closed` from Stripe `charge.dispute.*`, mapped to the seller order(s) of that charge.
Our outbox delivers these at least once with backoff; the portal dedupes on `eventId`.

## 7. Moderation (ADMIN.md)
`POST /listings/{id}/reports`, `POST /booking-messages/{id}/reports`, `POST /messages/{id}/reports` feed the portal admin queue.
Reviews: **GAP, requested** (C2): `POST /reviews/{id}/reports` (or a generic `content/reports` with `kind`) plus a moderation decision event back to us.

## 8. PII we hold
Buyer real email/name/addresses/phone live **only** in the marketplace. The portal gets relay email, display name, the shipping address and
optional phone. Retention and redaction rules for every field are in DECISIONS (M1 defines them; every later workstream extends the table).

## 9. Contract change requests to the seller portal
| Id | Request | Status |
|---|---|---|
| C1 | Catalog feed: signed listings/categories/brands/images endpoints, `listing.upserted`/`listing.unlisted` outbox events, signed product-question submission, `searchTerms[]` | **Merged** in portal main 73845da (docs/CATALOG.md); consumer slice: M2 |
| C2 | Content reports (`POST /content/reports`, kind review/listing/message) + `content.report_decided` | **Merged** in portal main 73845da (docs/CATALOG.md); consumer: M6 |
| C3 | Confirm webhook URL routing: one `Orders:WebhookUrl` for all event families | **Confirmed** 2026-11-04 |
| C4 | Webhook signature v2 over ts.id.type.body | Portal ROADMAP; until then consumers use the signed body id/type (we do) |
| C5 | Explicit derived `delivery {ships, localDelivery, serviceLocations[]}` on listings | Accepted, after C1; M2 falls back to the `delivery` attribute |
| C6 | Key /products/{id}/questions and /content/reports rate limits on the verified caller + target, not per IP (one marketplace caller for all shoppers) | Requested 2026-11-04 |
| C7 | Ingest gaps found by M3: (1) per-group speed in `shipping[]` (the order has one `shippingSpeed`; our groups can differ), (2) explicit `weightGrams` per variant in the catalog feed (weight-priced shipping), (3) a field for tax charged on shipping (M3 folds it into line `taxCents` for now) | Requested 2026-11-04 (M3 worker, via the orchestrator) |
| C8 | Does `refund.issued.amountCents` (and `order.cancelled.refundDueCents`) include the tax on the refunded items? The portal ledger excludes tax (PAYMENTS.md §2), the buyer paid it; M3 refunds items + shipping + proportional tax and reports items + shipping only, clamping to what is left either way | Question, 2026-11-04 (M3 worker, via the orchestrator) |
