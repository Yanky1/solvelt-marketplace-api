# Marketplace API reference

All errors are `{ "code": string, "message": string }`. Another user's resource is `404`, never `403`.
Rate limited endpoints answer `429 { code: "rate_limited" }` with `Retry-After`.

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | none | Liveness: `{ "status": "ok", "forwarding": { "invalid": n }, "outbox": { "deadLettered": n }, "admin": { "refused": n } }`; counts since start-up, for a monitor to alert on a rise |
| POST | `/api/portal/webhooks` | portal signature | Portal -> marketplace events; see below |
| GET | `/api/v1/catalog/categories` | none | Category tree with visible-listing counts; see Catalog |
| GET | `/api/v1/catalog/brands` | none | Brands that have visible listings |
| GET | `/api/v1/catalog/listings` | none | Paged, filterable, sortable listing cards |
| GET | `/api/v1/catalog/brands/{slugOrId}` | none | Brand storefront: seller, count, category tree |
| GET | `/api/v1/catalog/sellers/{id}` | none | Seller storefront: brands and category tree |
| GET | `/api/v1/catalog/sitemap/{kind}` | none | Paged keys + last-modified for sitemaps (`listings`, `categories`, `brands`, `sellers`) |
| GET | `/api/v1/catalog/suggest` | none | Autocomplete: listing, brand and category suggestions |
| GET | `/api/v1/catalog/listings/{slugOrId}` | none | One listing in full |
| POST | `/api/v1/auth/sign-up` | none | Create an account; `204` always for a valid body; see Auth |
| POST | `/api/v1/auth/resend-verification` | none | `204` always |
| POST | `/api/v1/auth/verify-email` | none | Body `{ token, password }`; `204` or `400 invalid_token` |
| POST | `/api/v1/auth/sign-in` | none | `200 { token, user }`; see Auth |
| POST | `/api/v1/auth/sign-out` | Bearer | `204`; ends this session |
| POST | `/api/v1/auth/sign-out-all` | Bearer | `204`; ends every session of this buyer |
| POST | `/api/v1/auth/forgot-password` | none | `204` always |
| POST | `/api/v1/auth/reset-password` | none | `204` or `400 invalid_token`; signs out everywhere |
| GET | `/api/v1/account/me` | Bearer | `{ id, displayName, email }` |
| GET | `/api/admin/outbox` | admin key | Outbox counts and recent messages (never payloads); see Operations |
| POST | `/api/admin/outbox/{id}/retry` | admin key | Re-queue a dead message; see Operations |
| GET | `/api/v1/ping` | none | Public rate limit bucket smoke endpoint (removed when the catalog endpoints land) |

## Headers the web server (BFF) must send
The API sees every buyer request coming from the web server, so rate limits need the end user's address, forwarded like this on **every** call:

| Header | Value |
|---|---|
| `X-Solvelt-Client-Ip` | The end user's IP literal (IPv4 or IPv6) as seen by the web server |
| `X-Solvelt-Client-Timestamp` | Unix seconds, web server clock (accepted within **60 s**: keep the web tier's clock in sync, e.g. NTP) |
| `X-Solvelt-Client-Signature` | `v1=` + lower-case hex `HMAC-SHA256(Web:ForwardingKey, "<timestamp>\n<ip>")` |

`Web:ForwardingKey` (env `Web__ForwardingKey`) is a dedicated key shared only by the web server and this API; it is never a portal secret and never reaches the browser.
Headers that are missing, malformed, stale or wrongly signed are ignored (the request is then limited together with every other unsigned request from the same peer), so always send all three.
If headers are present but invalid (a key mismatch or clock skew puts every user into one bucket) the API counts it, logs a warning at most once a minute (the reason only, never an address or signature) and shows the count on `GET /health` as `forwarding.invalid`: a non-zero and growing value means the web tier and the API disagree on the key or the time.
A captured header set can be replayed for 60 s to frame a victim's address, so **the API must be reachable only from the web tier, over TLS** (network policy or private networking); there is no nonce.
`Authorization: Bearer <session token>` is the buyer's session (see Auth). Every response may carry `X-Correlation-Id` on a `500`.

## `POST /api/portal/webhooks` (seller portal -> marketplace)
Called only by the seller portal's outbox dispatcher (CONTRACT.md §1.2, docs/ORDERS.md §3.4). Not for the web server or browsers.

Headers: `X-Solvelt-Timestamp` (unix seconds), `X-Solvelt-Signature: v1=<hex HMAC-SHA256(Portal:WebhookSecret, "<timestamp>.<raw body>")>`,
`X-Solvelt-Event-Id`, `X-Solvelt-Event-Type`. Body (at most 262144 bytes): `{ "id", "type", "createdAt", "data" }`.

Order of checks: body size, then signature and a 300 s timestamp window (nothing is parsed before they pass), then the envelope, then the event headers
against the signed body, then the projection (one database transaction).

| Status | `code` | Meaning |
|---|---|---|
| 200 `{ "status": "applied" }` | | a handler applied it |
| 200 `{ "status": "parked" }` | | its aggregate is unknown yet; stored, applied when the aggregate appears |
| 200 `{ "status": "stored" }` | | no handler for this type (forward compatible); stored and acknowledged |
| 200 `{ "status": "skipped" }` | | older than the applied state, or a terminal state must not reopen |
| 200 `{ "status": "duplicate" }` | | this event id was already received; nothing was done |
| 400 | `invalid_envelope` | not a JSON object, or `id`/`type`/`createdAt` missing or invalid (after a valid signature) |
| 400 | `event_id_mismatch`, `event_type_mismatch` | the unsigned `X-Solvelt-Event-*` header disagrees with the signed body |
| 401 | `unsigned`, `bad_timestamp`, `stale`, `bad_signature` | verification failed |
| 413 | `too_large` | body over 262144 bytes |
| 503 | `webhook_not_configured` | `Portal:WebhookSecret` is not set |
| 5xx | `internal_error` | a failure rolled the event back; the portal retries (backoff, 8 attempts, then dead-letter) |

## Catalog (public, anonymous, read-only)
All four endpoints use the `public` rate limit (headers: see "Headers the web server must send"). Money is whole **cents** (`long`), currency `USD`; times are UTC ISO 8601.
Only **visible** listings are ever returned (CONTRACT.md §2): last known portal status `Active`, seller active, and not unlisted. A listing that is not visible, or does not exist, is a `404 not_found`, with no hint which, with one exception: **a listing that WAS public (it was visible at some point) and has been unlisted, archived or removed answers `410 { "code": "gone" }`** (by slug or id), so search engines drop it quickly. A suspended seller's listings, drafts, listings first seen archived or unlisted (never public), listings we only heard an event about, and unknown ids stay `404`. Slugs never change (M1-32), so there is nothing to redirect except `/…/{id}` to the slug: the detail response by id carries the canonical `slug`.
**Unlisting is immediate**: a `listing.unlisted` portal event hides a listing at once, so catalog responses must not be cached for longer than a few seconds. They say so themselves: every `200` carries `Cache-Control: public, max-age=5`, every other status (including `429`) `no-store`.
Images are an id plus dimensions: the bytes come from the portal and are served by our own CDN path (a later workstream). Data comes from the portal catalog feed (provisional request C1; it also carries optional per-listing `searchTerms`, see M2-01); until it exists the API serves a seed of about 40 listings in Development.

### `GET /api/v1/catalog/categories`
The tree, roots first, ordered by the portal's order. Categories with no visible listing (in their subtree) are left out.
```json
[ { "id": "…", "slug": "food-drink", "name": "Food & Drink", "listingCount": 21,
    "children": [ { "id": "…", "slug": "bakery", "name": "Bakery", "listingCount": 8, "children": [] } ] } ]
```
`listingCount` counts visible listings in the category and everything below it.

### `GET /api/v1/catalog/brands?sellerId=`
Brands with at least one visible listing, by name (at most 500). `sellerId` (an id) limits to one seller; a malformed value is `400 invalid_query`.
```json
[ { "id": "…", "slug": "challah-corner", "name": "Challah Corner", "seller": { "id": "…", "displayName": "Mazel Bakery" }, "logoImageId": "…", "listingCount": 3 } ]
```

### `GET /api/v1/catalog/listings`
| Query | Meaning |
|---|---|
| `category` | A category slug or id; includes every category below it. An unknown one gives an empty page |
| `brand` | A brand slug or id; unknown gives an empty page |
| `seller` | A seller id (`400` when malformed) |
| `type` | `product` or `service` |
| `q` | Search text (at most 100 characters, else `400`). Split into words of Unicode letters and digits, lower-cased, with accents, Hebrew niqqud and cantillation dropped (pointed and unpointed Hebrew are the same word), Latin accents folded ("Crème" is "creme"), full-width forms and ligatures folded, a maqaf (־) acting as a word break and a geresh/gershayim/apostrophe not; at most 8 words of 40 characters. **Every** word must match and the last one matches as a prefix. Matches title, brand, the seller's search terms and the description, **weighted in that order**; an exact word beats a prefix. Chinese and Japanese have no spaces, so a run of characters is one word and matches by its beginning. When nothing matches, a typo-tolerant pass runs (words of 4+ characters; PostgreSQL uses trigram word similarity, the SQLite test fallback edit distance, so borderline typos can differ between them). Combines with every other filter, facet and sort: they apply to **all** matches (at most 50 000 per query, a safety bound: a query matching more is cut to its 50 000 most relevant, so for such a query the filtered totals and facet counts undercount: they describe those 50 000, not every match). Blank or missing = no search; punctuation only = no results |
| `priceMin`, `priceMax` | Whole cents, 0 to 1000000000000; `priceMin` above `priceMax` is `400`. A listing matches when some buyable price lies in the range (its price range overlaps it) |
| `available` | `true` leaves out sold-out listings (anything else is `400`) |
| `kosher` | Comma separated kosher certifications (`OU,Star-K`): alternatives (OR). Shortcut for `attr=kosher-certification:<value>` |
| `delivery` | Comma separated, each one of `ships`, `local`, `at-home`, `at-seller`, `online`: alternatives. Shortcut for `attr=delivery:<value>`; products carry `ships` and sometimes `local`, services `at-home` and/or `online` |
| `attr` | Repeatable `key:value` filter on any attribute (`attr=kosher-status:dairy`). Values of one key are alternatives (OR), different keys all have to hold (AND). At most 12 pairs, 10 values per key, keys up to 100 and values up to 60 characters, else `400` |
| `facets` | `true` adds a `facets` object (see below); anything but `true`/`false` is `400` |
| `sort` | `relevance` (default when `q` is given; without `q` it behaves as `newest`), `newest` (default otherwise), `best_selling` (placeholder: the newest order until M3 records sales), `price_asc`, `price_desc`, `title`. Equal sort values break on id. `page` numbers are positions: if listings change between two requests, a page can repeat or skip an item. Use `cursor` for a walk that never repeats or drops one |
| `cursor` | Opaque `nextCursor` of the previous page. It continues **the request it came from**: the same filters, `q` and sort (page size and `facets` may change), otherwise `400`; so do a cursor for another sort, a forged or malformed one, and a cursor together with `page`. A cursor is a position (sort value + id), so listings added or removed between two requests never shift or repeat items; but if the **sort value of a listing itself changes** between two pages (a price edit, a title change), that listing can appear twice or be skipped, as in any keyset paging. |
Every parameter except `attr` may be given once; a repeat is `400`. A control character (such as NUL) in any parameter except `q`, or in a cursor, is `400 invalid_query`; in a path it is `404`.
| `page` | 1-based, default 1 (at most 100000) |
| `pageSize` | Default 24, at most 100 |

A bad value is `400 { "code": "invalid_query", "message": … }`. Response:
```json
{ "items": [ {
    "id": "…", "slug": "classic-braided-challah", "type": "product", "title": "Classic Braided Challah", "summary": "Hand-braided, baked fresh every Thursday.",
    "priceFromCents": 650, "priceToCents": 950, "compareAtFromCents": 750, "currency": "USD", "available": true,
    "brand": { "id": "…", "slug": "challah-corner", "name": "Challah Corner" },
    "seller": { "id": "…", "displayName": "Mazel Bakery" },
    "category": { "id": "…", "slug": "bakery", "name": "Bakery" },
    "image": { "id": "…", "width": 1200, "height": 1200, "alt": "Classic Braided Challah, photo 1" } } ],
  "page": 1, "pageSize": 24, "total": 40, "totalPages": 2, "nextCursor": "…" }
```
`nextCursor` is `null` on the last page (the API fetches one extra row to know). With `cursor`, `page` is `0`; `total` and `totalPages` are still given. 

With `facets=true` the response also has (disjunctive: each facet is counted with every filter **except its own**, so it shows what choosing one of its values would give):
```json
"facets": { "categories": [ { "id": "…", "slug": "food-drink", "name": "Food & Drink", "listingCount": 21, "children": [ … ] } ],
  "brands": [ { "id": "…", "slug": "challah-corner", "name": "Challah Corner", "count": 3 } ],
  "sellers": [ { "id": "…", "displayName": "Mazel Bakery", "count": 8 } ],
  "types": [ { "value": "product", "count": 28 } ], "available": 38,
  "price": { "minCents": 600, "maxCents": 48000 },
  "attributes": { "kosher-certification": [ { "value": "OU", "count": 20 } ], "kosher-status": [ … ], "kosher-for-passover": [ … ], "delivery": [ … ] } }
```
`price` is `null`/`null` when nothing matches; counts are listings (a listing with two values of an attribute counts once for each). Rating facets arrive with M6.
`priceFromCents`/`priceToCents` are the range over the variants that can be bought now (over all variants when everything is sold out); `compareAtFromCents` is the lowest struck-through price above the price, or `null`; `available` is false when every variant is sold out (the listing is still shown); `image` is `null` when there is none.

### `GET /api/v1/catalog/brands/{slugOrId}` and `GET /api/v1/catalog/sellers/{id}`
Storefront data for the brand and seller pages; `404` when the brand or seller is unknown, suspended, or has no visible listing. Listings come from `GET /listings?brand=` / `?seller=` (which also takes every filter, facets and cursors).
```json
{ "id": "…", "slug": "challah-corner", "name": "Challah Corner", "logoImageId": "…", "seller": { "id": "…", "displayName": "Mazel Bakery" }, "listingCount": 3,
  "categories": [ { "id": "…", "slug": "food-drink", "name": "Food & Drink", "listingCount": 3, "children": [ … ] } ] }
{ "id": "…", "displayName": "Mazel Bakery", "listingCount": 11, "brands": [ { "id": "…", "slug": "…", "name": "…", "seller": { … }, "logoImageId": "…", "listingCount": 3 } ], "categories": [ … ] }
```

### `GET /api/v1/catalog/sitemap/{kind}?page=&pageSize=`
Every page carries `ETag` and `Last-Modified` (its newest entry, to the second) and answers `304` to `If-None-Match` (or, when that is absent, `If-Modified-Since`).
`kind` is `listings` (key = slug, last modified = the portal's updated time), `categories` and `brands` (key = slug; last modified = the newer of the row's own change and the newest update among their visible listings, a category counts its whole subtree and the changes of the categories in it, so it does not move backwards when a listing leaves) or `sellers` (key = id; same rule). Only entries with at least one visible listing appear, so an unlisted listing leaves at once. `pageSize` 1 to 5000 (default 1000), ordered by id so pages are stable; anything else `400`, an unknown kind `404`.
```json
{ "kind": "listings", "page": 1, "pageSize": 1000, "total": 40, "totalPages": 1, "items": [ { "key": "classic-braided-challah", "lastModified": "2026-09-04T09:00:00+00:00" } ] }
```
The web tier builds `sitemap.xml` (an index of `totalPages` child sitemaps per kind) from this; it must not cache the answers for more than a few seconds beyond what its own sitemap cache needs.

### `GET /api/v1/catalog/images/{imageId}`
The original bytes of an image of a **visible** listing, for the web tier's resizer (the browser never calls it; the web server serves resized copies under `/img`). The `imageId` is the `image.id` of a listing card or detail. Anything that is not a visible listing's image (unknown id, malformed id, a hidden, unlisted or suspended-seller listing, an image the portal no longer has) is the same `404 not_found`, and the portal is not asked. The visibility check runs on **every** request, before any cache, so an unlisted listing's images stop being served at once (the web tier keeps its own copies for at most a day).

| Answer | When |
|---|---|
| `200` + bytes | `Content-Type` is the stored type, one of `image/jpeg`, `image/png`, `image/webp`, `image/gif`, `image/avif`. Headers: `X-Content-Type-Options: nosniff`, `Content-Disposition: inline`, `Content-Security-Policy: default-src 'none'; sandbox`, `Cache-Control: public, max-age=300`, a strong `ETag` |
| `304` | `If-None-Match` matches the `ETag` (no body) |
| `404 not_found` | see above |
| `502 bad_image` | the portal's answer is not a raster picture (any other type, **SVG included**, or empty) or is over 10 MB |
| `503 image_unavailable` | the portal could not be reached; `Retry-After: 30`, `no-store` |

Bytes come from the portal (`GET /catalog/images/{id}`, signed, server-side only) and are kept in a bounded memory cache (64 MB, 10 minutes). With the seed fixture there is no portal: the endpoint serves a generated placeholder (a PNG of the stored width and height, at most 1200 px a side, made from the id alone; a raster, because the web resizer refuses SVG).

### `GET /api/v1/catalog/suggest?q=&limit=`
Autocomplete for the header search box. Below two characters (after normalising) the answer is an empty list. `limit` is 1 to 10 (default 8, else `400`); `q` at most 100 characters.
```json
{ "query": "chal", "suggestions": [
  { "kind": "listing", "text": "Classic Braided Challah", "slug": "classic-braided-challah" },
  { "kind": "brand", "text": "Challah Corner", "slug": "challah-corner" },
  { "kind": "category", "text": "Bakery", "slug": "bakery" } ] }
```
Listings come first (best matches, visible only), then at most three brands and three categories whose name has a word starting with the last typed word (brands and categories with at least one visible listing in them or below). It uses the `public` limit like every catalog call, so the web tier should debounce (about 150 ms). Search text is never logged.

### `GET /api/v1/catalog/listings/{slugOrId}`
`slugOrId` is the listing's slug (stable: it does not change when the title does) or its id.
```json
{ "id": "…", "slug": "classic-braided-challah", "type": "product", "title": "…", "summary": "…", "description": "…",
  "priceFromCents": 650, "priceToCents": 950, "compareAtFromCents": 750, "currency": "USD", "available": true,
  "brand": { "id": "…", "slug": "…", "name": "…" }, "seller": { "id": "…", "displayName": "…" },
  "categoryPath": [ { "id": "…", "slug": "food-drink", "name": "Food & Drink" }, { "id": "…", "slug": "bakery", "name": "Bakery" } ],
  "variants": [ { "id": "…", "name": "Small", "priceCents": 650, "compareAtCents": 750, "available": true } ],
  "images": [ { "id": "…", "width": 1200, "height": 1200, "alt": "…" } ],
  "attributes": [ { "key": "kosher-certification", "value": "OU" } ],
  "faqs": [ { "question": "Is this kosher?", "answer": "…" } ],
  "createdAt": "2026-09-01T09:00:00+00:00", "updatedAt": "2026-09-04T09:00:00+00:00" }
```
`categoryPath` runs from the root to the listing's category (the breadcrumb). `variants` are in the seller's order; add to cart by **variant id** (never by SKU). Attributes are free-form key/value pairs; kosher information uses the keys `kosher-certification`, `kosher-status` (`meat`, `dairy`, `pareve`, `mevushal`) and `kosher-for-passover`.

### Portal events that change the catalog
`listing.upserted` and `listing.unlisted` (`data: { listingId, version }`) arrive on `POST /api/portal/webhooks`: an upsert records that a newer version exists and the sync job fetches it; an unlist hides the listing immediately and keeps it hidden against any snapshot of that version or older.

## Operations (outbox)
For operators and the future staff area (M7), not for browsers, and **never through the public host**: the endpoints exist only on `Admin:Host` (decision O-12; the web server must never proxy `/api/admin`). Send `X-Solvelt-Admin-Key: <Admin:ApiKey>` (env `Admin__ApiKey`, at least 32 characters, its own key). Everything that is not an authorised request on the admin host (no key configured, a blank key, another host, a missing or wrong key) answers **`404`**. Own rate limit (`RateLimits:AdminPerMinute`, default 30 per client IP). Refusals show as `admin.refused` on `GET /health`, next to `outbox.deadLettered`.

### `GET /api/admin/outbox?status=pending|delivered|dead&limit=1..200&cursor=`
`200 { counts: { pending, delivered, dead }, messages: [{ id, type, status, attempts, nextAttemptAt, lastError, createdAt, deliveredAt, deadAt }], nextCursor }`, newest first (default 50 per page). `nextCursor` is null on the last page; pass it back as `cursor` for the next one (a bad cursor is `400 invalid_cursor`). `lastError` is a short code (for example `portal_422_exceeds_sale`, `portal_401`, an exception type such as `TimeoutException`). The payload is never returned. An unknown `status` is `400 invalid_status`.

### `POST /api/admin/outbox/{id}/retry`
`204`: a dead message goes back to pending with 0 attempts and is due now; `lastError` keeps saying why it died until the next attempt replaces it. `409 not_retryable`: the message is not dead, or its payload was already purged (30 days after dead-lettering). `404`: unknown id.

### Delivery rules
At least once. A failed message is retried after 30 s, 60 s, 120 s ... doubling, capped at 1 h, and is dead after 8 failures, or at once when waiting cannot help: a portal `401` (fix the key, then retry from here), `413`, `422` and other 4xx. A portal `429` waits at least 60 s. Configuration: `Outbox:MaxAttempts`, `BackoffSeconds`, `MaxBackoffSeconds`, `LeaseSeconds` (must be at least the portal total deadline and the email timeout plus 30 s, checked at start-up), `BatchSize`, `PollSeconds`, `DeliveredPayloadRetentionDays`, `DeadPayloadRetentionDays`, `RowRetentionDays`.

## Services (M4)
### `GET /api/v1/services/{listingId}/availability`
Public, public rate limit. Query: `location` (`online|customer|seller`, required), `from`/`to` (dates or instants; snapped to UTC days; default today + 14 days; at most 62 days; `from` is never in the past), `postalCode`, `lat`+`lng` (both), `providerId`, `minutes` (hourly services), `addOns` (comma separated ids).
`200 { durationMinutes, priceCents, providers: [{ providerId, timeZone }], slots: [{ startAt, endAt, providerIds }], from, to, fetchedAt }`, slot times are UTC instants sorted ascending; show them in the provider's `timeZone` and the buyer's zone. `Cache-Control: public, max-age=15` on a 200 only (every other answer is `no-store`); the server caches the portal answer at most 30 s, so what a shopper sees can be up to about 45 s old. Coordinates are rounded to 2 decimals (about 1.1 km) and dates to whole UTC days; `from`/`to` beyond 400 days ahead are `422`. The marketplace itself allows at most `Services:AvailabilityPortalCallsPerMinute` (300) portal calls per service per minute (cache misses only); beyond that the answer is `503 availability_unavailable` until the bucket refills.
Errors: `404` not a visible service; `422 validation_failed` (field errors) or a portal-validated `outside_service_area | location_not_offered | duration | add_on | provider_required`; `503 availability_unavailable` (retry).

### `GET /api/v1/services/{listingId}/options`
Public. `200 { available, version, addOns: [{ id, name, description?, priceCents, extraMinutes }], intakeQuestions: [{ id, label, kind: text|longText|choice|yesNo|number, required, choices?, maxLength? }], providers: [{ providerId, displayName, photoImageId? }], assignment: leastBusy|roundRobin|buyerChoice, policy: { cancelFreeHours, cancelFeePercent, noShowFeePercent, maxReschedules, approvalHours }, travelFeeCents, serviceAreaPostalCodes?, serviceRadiusKm? }`. `available:false` with empty lists means the portal does not provide options yet: show the widget without add-ons and intake questions. `404` not a visible service, `503 options_unavailable`. `Cache-Control: public, max-age=60`. Send `addOnIds` and `intakeAnswers` (`checkout`) only with ids from this list; the portal validates them.

### Quotes (signed-in buyer, `Cache-Control: private, no-store`)
| Call | Body | Answer |
|---|---|---|
| `POST /api/v1/quotes` (header `Idempotency-Key`) | `{ listingId, location, details (10-2000 characters), address? (required for `customer`), phone?, preferredTimes? (max 5, future) }` | `201 { created: true, quote }` / `200 { created: false, quote }`; `409 idempotency_conflict`, `404`, `422 validation_failed | too_many_quotes | outside_service_area`, `503 quote_unavailable` (retry with the SAME key) |
| `GET /api/v1/quotes`, `GET /api/v1/quotes/{id}` | - | `QuoteView`: `id, listingId, status` (`Requested | Quoted | Accepted | Declined | Withdrawn | Expired`), `location, details, preferredTimes, currency, priceCents, durationMinutes, slots` (the offered times, UTC), `providerId, expiresAt, expired, note, declineReason, bookingId, createdAt`. Another buyer's quote is `404` |
| `POST /api/v1/quotes/{id}/accept` | `{ start, addOnIds?, intake?, phone? }` (`start` is one of `slots`; there is no price field) | `201 { created, quote, booking }` (or `200` repeated): the booking is a **Hold** (portal C13: pay with `POST /bookings/{id}/checkout`, then it is confirmed like any hold; if the hold lapses the quote goes back to `Quoted` and can be accepted again until its own `expiresAt`); a quote accepted before the portal change keeps an already Confirmed (or Requested) booking that **must be paid within 30 minutes** or it is cancelled; `409 quote_expired | quote_not_open | slot_unavailable`, `422 validation_failed | slot_not_offered` |
| `POST /api/v1/quotes/{id}/decline` | `{ reason? }` | `200 { quote }` (repeatable); `409 quote_not_open` |

### Bookings (signed-in buyer; `Authorization: Bearer`, `Cache-Control: private, no-store`)
| Call | Body | Answer |
|---|---|---|
| `POST /api/v1/bookings/holds` (header `Idempotency-Key`: 8-100 characters of `A-Za-z0-9-_.:`) | `{ listingId, start, location, durationMinutes?, providerId?, addOnIds?, address?, phone? }`; `address` `{ name?, line1, line2?, city, region?, postalCode, country, lat?, lng? }` is required for `customer`. No price, total or end time exists: send the `start` of a slot from the availability list | `201 { created: true, booking }` (or `200 { created: false, booking }` for a repeated key); `409 slot_unavailable` (someone else holds it: choose another), `409 idempotency_conflict`, `404` (not a service), `422 validation_failed`, `422 too_many_holds`, `422 outside_service_area | location_not_offered | duration | add_on | provider_required | quote_required`, `503 booking_unavailable` (retry with the SAME key) |
| `POST /api/v1/bookings/{id}/checkout` | `{ intake?: [{ id, answer }] (max 30), phone? }` (no amount exists) | `200 { booking, payment: { provider: "stripe", clientSecret, publishableKey } }` the SAME intent when repeated or sent twice in parallel; `404`, `409 hold_expired` (choose a time again), `409 already_paid`, `409 not_payable`, `422 validation_failed | amount_too_small | payment_rejected`, `503 payments_unavailable`. The booking is confirmed (`status` becomes `Confirmed` or `Requested`) after Stripe reports the payment: poll `GET /bookings/{id}` |
| `GET /api/v1/bookings/{id}/cancel-preview` | - | `{ feeKnown, feeCents?, refundCents?, totalCents, withinFreeWindow, freeUntil?, currency }`; `feeKnown:false` until the provider's policy is known: then say "the provider's policy applies", never show a number |
| `POST /api/v1/bookings/{id}/cancel` | `{ reason? }` | `200 { booking }` (idempotent); money follows from the provider's event; `409 invalid_state`, `404`, `503 booking_unavailable` |
| `POST /api/v1/bookings/{id}/reschedule` (header `Idempotency-Key`: 8-60 of `A-Za-z0-9-_`) | `{ newStart }` (a start from the availability list) | `200 { booking }` (`Rescheduled`, or unchanged with `rescheduleRequestedStartAt` while the provider decides); `409 slot_unavailable | reschedule_limit | too_late | invalid_state`, `422` |
| `POST /api/v1/bookings/{id}/proposal` | `{ accept }` | `200 { booking }`; `409 no_proposal` |
| `POST /api/v1/bookings/{id}/acknowledge-reschedule` | - | `200 { booking }` clears `requiresBuyerConsent` (cancelling instead is free); `409 nothing_to_accept` |
| `POST /api/v1/bookings/{id}/extra-charge/accept` | `{ expectedAmountCents }` (required): the amount the buyer saw; missing is `422` with `errors.expectedAmountCents`, a different current amount is `409 amount_changed`, and either way nothing is consented or charged (the amount itself is always the provider's) | `200 { amountCents, payment: { provider, clientSecret, publishableKey } }` (same intent when repeated); `409 nothing_requested | already_paid`, `422 amount_too_small`, `503` |
| `POST /api/v1/bookings/{id}/extra-charge/decline` | - | `204`; `409 nothing_requested` |
| `GET /api/v1/bookings/{id}/calendar.ics` | - | `text/calendar` attachment for a Requested/Confirmed/Rescheduled/InProgress booking (no address, no meeting link); `404` otherwise |
| `GET /api/v1/bookings` | - | `{ bookings: [BookingView] }` newest first, no meeting link |
| `GET /api/v1/bookings/{id}` | - | `BookingView`; another buyer's booking or a failed hold attempt is `404` |

`BookingView`: `id, number, listingId, status` (`Hold | Requested | Confirmed | Rescheduled | InProgress | Completed | Declined | Expired | CancelledByBuyer | CancelledBySeller | NoShow`), `location, startAt, endAt, durationMinutes, currency, priceCents, addOnsCents, travelFeeCents, totalCents` (the portal's amounts), `holdExpiresAt` + `holdExpired` (show a 10 minute countdown), `approvalDeadline, proposedStartAt, cancellationFeeCents, refundDueCents, noShowFeeCents, additionalChargeCents, rescheduleCount, addOnIds, onlineInstructions` (the meeting link: detail view of a confirmed booking only), `failureCode, createdAt`. `policy` (since portal C11, the cancellation policy the portal froze on the booking: `{ cancelFreeHours, cancelFeePercent, noShowFeePercent, maxReschedules, approvalHours }`, each `null` when unknown; `null` as a whole until the portal has told us; never the service's current policy). Also `paymentDueAt` and `needsPayment` (a booking that is Confirmed/Requested/Rescheduled but unpaid, made from a quote accepted before portal C13: pay by `paymentDueAt` or it is cancelled; `null`/`false` for a hold, which has `holdExpiresAt`, and once paid), and `listingSlug` (on `GET /bookings` and `GET /bookings/{id}`: the service page is `/p/{slug}`).

## Auth (buyers)
All bodies are JSON. The web server holds the session token in its own HttpOnly cookie and sends `Authorization: Bearer <token>`. Auth endpoints share the strict per-client-IP rate limit (`RateLimits:AuthPerMinute`, default 10), forwarded as described above. This is the contract the web app assumed (its W1-21), confirmed.

| Endpoint | Body | Answers |
|---|---|---|
| `POST /api/v1/auth/sign-up` | `{ email, password, displayName }` | `204` for a new, an unverified and an already registered address (same empty body); `422 { code: "validation_failed", errors: { email?, password?, displayName? } }` |
| `POST /api/v1/auth/verify-email` | `{ token, password }` | `204`, or `400 { code: "invalid_token" }` (unknown, used, expired, wrong kind, **or a wrong password**: the link is then not used up); the sign-in gate never answers `429` here (a delayed valid token, a wrong password and an invalid token are the identical `400`), but the endpoint's per-client rate limit can (`429` with `Retry-After`, whatever the token); `422` on `password` when it is missing. The password is the one chosen at sign-up (decision O-16). Someone who does not know it uses forgot-password |
| `POST /api/v1/auth/resend-verification` | `{ email }` | `204` always; a new link replaces older ones; at most 3 mails an hour per address |
| `POST /api/v1/auth/sign-in` | `{ email, password }` | `200 { token, user: { id, displayName } }`; `401 invalid_credentials` (wrong password and unknown address are identical); `403 email_not_verified` (only after the correct password); `429 rate_limited` with `Retry-After` (the address is delayed or the client's hourly failure budget is spent; the same for a known and an unknown address) |
| `POST /api/v1/auth/sign-out` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/sign-out-all` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/forgot-password` | `{ email }` | `204` always; mail for any existing account, verified or not; the same database work for an unknown address |
| `POST /api/v1/auth/reset-password` | `{ token, password }` | `204`, or `400 invalid_token`, or `422` for a weak password or one equal to the address (the link is not used up); every session of the account ends; the address is marked verified and its sign-in delay is cleared |
| `POST /api/v1/auth/reauthenticate` | `{ password }` (Bearer) | Step-up: `204` and the session's `authenticated_at` is now; `401 invalid_credentials`; `429` with `Retry-After`; `422` when the password is missing. Same gate as sign-in |
| `POST /api/v1/auth/session/rotate` | none (Bearer) | `200 { token }`; the old token is revoked; the absolute expiry and `authenticated_at` carry over; `401` when the session is gone |
| `GET /api/v1/account/me` | none | `200 { id, displayName, email }`, or `401 { code: "unauthorized" }` |

Rules: email is trimmed, in Unicode normal form C and lower-cased (the same address typed with a combining accent or the precomposed letter is one address; names and address lines are stored in normal form C too, and passwords are hashed in it, so either spelling signs in); password 10 to 128 characters and not the email address; display name 1 to 60 characters. The token returned by sign-in is 43 characters of base64url, shown once. Sessions end after 7 idle days (sliding) or 30 days in all. Sign-in is **delayed, not locked** (decision M1-63): six failures per address with no delay, then the next attempt must wait 1 s, 2 s, 4 s ... up to 15 minutes, the same for a known and an unknown address; a right password or a password reset clears it; each client IP may fail 20 times an hour across all addresses. Auth request bodies are limited to 16 KB (`413`). Links in mails open `<Marketplace:PublicBaseUrl>/verify-email?token=...` and `/reset-password?token=...` (24 h and 60 min; the token is `<32 hex>.<43 base64url>` and is opaque to the web: pass it through unchanged); the pages must POST the token (a mail scanner opening the link then cannot use it up). The buyer is known to the portal only as a relay address and a name like "Jane B." (decision M1-55 and the PII table).

## Account (buyers)
All endpoints need `Authorization: Bearer <token>` (`401 unauthorized` otherwise). Every response under `/api/v1/auth` and `/api/v1/account`, refusals included, carries `Cache-Control: private, no-store`.

| Endpoint | Body | Answers |
|---|---|---|
| `GET /api/v1/account/profile` | none | `200 { id, displayName, email, phone }` |
| `PUT /api/v1/account/profile` | `{ displayName, phone? }` | `200` the profile; `422 validation_failed` with `errors` per field |
| `GET /api/v1/account/addresses` | none | `200 [address]`, default first then oldest |
| `POST /api/v1/account/addresses` | `{ fullName, line1, line2?, city, region?, postalCode, country, phone?, lat?, lng?, isDefault? }` | `201` the address; `422`; `409 too_many_addresses` (20); the first address is always the default |
| `GET/PUT/DELETE /api/v1/account/addresses/{id}` | as above for PUT | `200`/`204`; `404` for another buyer's, unknown or malformed ids; deleting the default promotes the oldest remaining |
| (address writes under contention) | | `503 too_busy` with `Retry-After: 1` when the write kept meeting a lock or unique conflict after its retries (never a 500); repeat the request (M1-86) |
| `POST /api/v1/account/addresses/{id}/default` | none | `204`; the address is now the only default; `404` for another buyer's, unknown or malformed ids |
| `POST /api/v1/account/password` | `{ currentPassword, newPassword, removePasskeys? }` | `200 { token }`: EVERY session ends, the caller's too, and the token is the fresh session (M1-87; it was `204` and kept the caller's session: replace the stored token with this one) (`removePasskeys: true` also removes every passkey in the same step; by default passkeys are kept, M1-85); `403 reauthentication_required` (sign in again within 10 minutes: call `/auth/reauthenticate`, then repeat); `401 invalid_credentials`; `409 account_changed` (the account changed while this ran, for example a reset: sign in again); `422` weak password; `429` with `Retry-After`; other sessions are revoked |
| `POST /api/v1/account/delete` | `{ password }` | `204`; same errors as the password change (including `409 account_changed`); the session and every other one stop at once and the email address is free for a new sign-up at once, the rest of the personal data is erased by a job shortly after |
| `POST /api/v1/account/data-export` | (no body) | `200` a JSON file `{ generatedAt, sections }` (attachment, no-store) with the buyer's own data per module (`account`, `messages`, `notifications`, `cancellations-and-returns`, and whatever M3/M4 register); `403 reauthentication_required` when the sign-in is older than 10 minutes; `401` without a session. Decision M5-32. |

US addresses (`country: "US"`) need a 5 or 9 digit ZIP and a state code; other countries a plain postal code. `lat` and `lng` come together or not at all.

## Cart (M3)
`/api/v1/cart`, public rate limit. The owner is the signed-in buyer (`Authorization: Bearer`) or a guest (`X-Solvelt-Cart-Token`). A guest cart is created by the first add; the answer carries `cartToken` **once** (43 base64url characters; the web server keeps it in its own HttpOnly cookie). An unknown guest token never becomes a cart id: it gets a fresh cart and a fresh token. A present but invalid Bearer is `401` (never a silent guest cart).

| Endpoint | Body | Answers |
|---|---|---|
| `GET /api/v1/cart` | none | `200 CartView` (an empty cart when none exists) |
| `POST /api/v1/cart/lines` | `{ variantId, quantity? = 1 }` | `200 CartView`; `404` hidden, unlisted, suspended-seller or unknown variant; `422 not_purchasable` (service or sold out), `quantity_limit` (1..20), `cart_full` (50 lines), `currency_mismatch` (the marketplace sells in USD only), `503 cart_busy` (retry). Adding a variant already in the cart adds to its line (capped at 20) |
| `PATCH /api/v1/cart/lines/{lineId}` | `{ quantity }` (0 removes) | `200 CartView`; `404` for a line that is not in this cart; `422 quantity_limit` |
| `DELETE /api/v1/cart/lines/{lineId}` | none | `200 CartView`; `404` |
| `POST /api/v1/cart/merge` | `{ guestToken }`, Bearer required | `200 CartView`: moves the guest cart into the buyer's (quantities add, capped at 20). Call it right after sign-in; repeating it or an unknown token changes nothing |

`CartView = { currency, groups: [{ sellerId, sellerName, brandId, brandName, subtotalCents, lines: [{ id, listingId, variantId, slug, title, variantName, imageId, quantity, unitPriceCents, compareAtCents, lineTotalCents, status: ok|unavailable, previousUnitPriceCents }] }], itemCount, subtotalCents, canCheckout, cartToken }`. Groups are per (seller, brand), exactly as the portal splits seller orders. Everything is read from the catalog on every call: a client-supplied price, total or name is not an input anywhere. `unavailable` lines (hidden, unlisted, seller inactive, sold out, gone) count as 0 and make `canCheckout` false until removed. `previousUnitPriceCents` is set when the price differs from the one the buyer last saw (changing the quantity accepts the new price). **A price change does not block checkout**: the web shows the new price and sends the quote's `quoteHash` with the checkout (below). Every cart and checkout answer carries `Cache-Control: private, no-store`; keep `cartToken` only in an HttpOnly cookie of the web server and never in loader data.

## Checkout quote (M3)
`POST /api/v1/checkout/quote`: prices the cart for an address and the delivery choices made so far. Same owner rules as the cart (Bearer, or `X-Solvelt-Cart-Token`). Safe to repeat; reserves nothing. **The body has no money field and none is read.**

Body: `{ address: { country (2 letters), region? (state, needed for US tax), postalCode, lat?, lng? }, selections?: [{ sellerId, brandId, speed? | localWindowId? + localDate? }] }`. A group with no selection gets its cheapest carrier speed. A selection names a speed the portal offers, **or** a local window from the quote's own `local.windows` (not both).

`200 { currency, groups: [{ sellerId, sellerName, brandId, brandName, lines: [{ lineId, variantId, title, quantity, unitPriceCents, lineTotalCents, taxCents }], itemsCents, shippingOptions: [{ speed, priceCents, free, estimate: { earliest, latest } }], unavailableSpeeds: [{ speed, reason: "weight_unknown" }], local: { eligible, reason, feeCents, timeZone, windows: [{ windowId, date, start, end, remaining }] } | null, selected: { kind: carrier|local, speed?, windowId?, date?, shippingCents } | null, shippingCents, shippingTaxCents, taxCents, totalCents }], itemsCents, shippingCents, taxCents, totalCents, canPay }`. `canPay` is false when a group has nothing to ship with (a destination outside every zone, or only weight-priced speeds while a line's weight is unknown: `unavailableSpeeds` says why; local delivery can still be chosen). Every total is the exact sum of its parts.
Errors: `401`, `422 validation_failed` (address), `422 invalid_selection` (`detail` in `message`: unknown group, speed not offered, window not offered or full, speed mixed with a window), `409 cart_not_ready` (empty cart or an unavailable line), `503 shipping_unavailable` (the portal's settings could not be read; try again).

## Checkout and payment (M3)
`POST /api/v1/checkout` (a Bearer, OR a guest: `X-Solvelt-Cart-Token` plus `email` in the body; a guest gets `guestOrderToken` in the answer and the same link by mail once paid; decision M3-32). Body: `{ address: { name, line1, line2?, city, region?, postalCode, country, phone?, lat?, lng? }, selections?: [...as in the quote], giftMessage? (<= 500), quoteHash, email? (guests) }`. `quoteHash` (required) is the token of the quote the buyer saw (`quote.quoteHash`, valid until `quote.expiresAt`, 15 min): if it does not match the server's fresh quote (price, tax, shipping, delivery choice or destination changed) or has expired, nothing is created and the answer is `409 { code: "price_changed", quote }` (show the new quote, ask the buyer to confirm, send again with its hash). It is a consent check only, never charged. Intents accept cards and wallets only. `409 checkout_in_progress` means a payment of this cart is still being processed (a delayed method): wait, do not change the checkout. A US address needs a valid state code (`422`); a total above 99,999,999 cents is `422 amount_too_large`. **No money field exists; none is read.** It prices the cart exactly like the quote, then:
* creates our order `MP-yyyyMMdd-XXXXXXXX` as `pending_payment` (one group per seller and brand, lines with the server's prices), and
* creates the ONE Stripe PaymentIntent for the order total (Stripe idempotency key = `pi:<order number>`).
`200 { orderNumber, status, quote: <as in the quote>, payment: { provider: "stripe", clientSecret, publishableKey } }`: the web mounts the Payment Element with `clientSecret` and `publishableKey`. Repeating the same request (double click, retry, a parallel duplicate) returns the **same** order and the same `clientSecret`; changing the address, a delivery choice, a quantity or a price (the catalog moved) is a new order and the previous pending one is superseded (its intent is cancelled).
Errors: `401`; `422 validation_failed` (field errors keyed `address.name`, `address.line1`, ..., `giftMessage`); `422 invalid_selection`; `422 amount_too_small` (Stripe's 50 cent minimum); `409 cart_not_ready`; `409 delivery_unavailable` (nothing can ship to that address); `409 order_not_pending` (that exact checkout was already paid), `409 checkout_superseded` (another checkout of the same cart replaced this one while its payment was being prepared: reload the cart and start again; a cart has at most one payable order); `502 payment_rejected`; `503 payments_unavailable` / `shipping_unavailable` (nothing was charged; the order, if created, stays pending and the same request retries it).
`GET /api/v1/checkout/orders/{number}` (Bearer): the buyer's own order with status (`pending_payment`, `superseded`, `paid`, `placed`, `failed`, `expired`), totals and groups; another buyer's order is `404`. The confirmation page polls it after the payment (the order becomes `paid` when Stripe's webhook arrives).
Configuration (env only): `Stripe__SecretKey` (`sk_test_`/`rk_test_`), `Stripe__PublishableKey` (`pk_test_`), `Stripe__WebhookSecret` (its own key, >= 32 chars, required with a secret key outside Development/Testing). A live key (`sk_live_`, `rk_live_`, `pk_live_`) stops start-up in every environment. Without a key, Development/Testing use an in-process fake processor and other environments answer `503 payments_unavailable`.

## Stripe webhook (M3, called by Stripe, not by the web)
`POST /api/stripe/webhooks`, anonymous, body cap 256 KB. Verified with `Stripe:WebhookSecret` (its own key) over the raw body: header `Stripe-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, "t.body")>`, 5 minute window, several `v1` accepted. Wrong, missing or stale signature: `400 bad_signature`; secret unset: `503`. Answer `200 { received, status: applied|ignored|unmatched|duplicate }`. Idempotent on the event id (`stripe_events`); a handler failure is `5xx`, so Stripe retries. Register the endpoint for `payment_intent.succeeded`, `payment_intent.payment_failed`, `charge.refunded` and `charge.dispute.created|updated|closed|funds_withdrawn|funds_reinstated`.
* `payment_intent.succeeded`: the order is found by the intent id **and** its `marketplace_order_id` metadata (both must agree); it becomes `paid` only from `pending_payment`, the cart lines it bought are removed, and `order.ingest` is queued. A payment on a superseded or expired order is refunded in full automatically (order `failed`, `superseded_payment` / `expired_payment`, buyer emailed) and never sent to the seller. The amount and currency must equal the priced order, otherwise the order is `failed` (`amount_mismatch`) and what was taken is refunded.
* `payment_intent.payment_failed`: only the decline code is kept while the order is still pending (the buyer may retry); a late failure never changes a paid order.
* Events that are not ours (no `marketplace_order_id`) are acknowledged as `unmatched`; an event naming an order we have not saved yet is a `5xx` so Stripe sends it again.
Order lifecycle seen by the web: `pending_payment` -> `paid` (webhook) -> `placed` (portal accepted; seller order numbers visible) or `failed` (`failureCode`, full refund, buyer emailed). The order endpoint returns no failure details beyond the status; the page shows a generic text per status.
* `charge.refunded`: a refund made outside this system (the Stripe dashboard) is recorded against the order's refund cap and logged; refunds we made are recognised and ignored.
* `charge.dispute.*`: forwarded to the portal as `dispute.created|updated|closed` (with `createdAt` = Stripe's event time), one per seller order of the payment (amount = the seller share of the dispute: proportional to each group's charged total, capped at what that seller order sold (items + shipping, no tax) with the cut moved to groups that still have room; the dispute fee, when Stripe tells us, is split the same way; decision M3-26). Older events and anything after `closed` are dropped. A dispute spanning several seller orders goes out at once, one event per seller order (portal C10 is merged; `Portal:PerSellerOrderDisputes` is on by default and only an emergency off switch, decision M3-33).
Portal webhooks (`/api/portal/webhooks`) now also act on `refund.issued` and `order.cancelled` (with `refundDueCents`): the buyer is refunded through Stripe EXACTLY the amount the portal states (it already includes line tax and shipping) and the portal is told with `refund.processed`, whose `amountCents` excludes the tax (backed out per line, floor proration; decision M3-16). With `Portal:PerGroupShipping` on (portal C8, brief N8) the events also carry `shippingTaxCents` (the amount = lines with their tax + `shippingCents` net + `shippingTaxCents`; `taxCents` = line tax + shipping tax): it is used as sent, capped at what is left of the seller order's shipping tax, and only inferred (proportional, the last shipping refund taking the remainder) when an older portal omits it; decision M3-27.

## Buyer orders (M5)
All routes need the buyer session (`Authorization: Bearer`), answer `Cache-Control: private, no-store`, and answer 404 for another buyer's order.
* `GET /api/v1/orders?limit=1..50&before=<createdAt>&beforeId=<id>` → `{ orders: [{ number, createdAt, totalCents, refundedCents, groups: [{ sellerOrderNumber, sellerName, status, itemCount, firstItem }] }], next }`.
* `GET /api/v1/orders/{number}` → the order, per seller order: `status` (`processing | new | accepted | on_hold | partially_shipped | shipped | delivered | cancelled | refunded`), lines with `shippedQty/returnableQty/...`, `shipments`, `returns` (with a live `label` only), `cancelRequest`, `refunds` (shown, never moved here), and `canCancel / canReturn / canConfirmDelivery`.
* `GET /api/v1/guest-orders/{number}` (header `X-Guest-Order`: the order-access link; no sign-in) → a guest's read-only progress view (per seller order: status and dates, lines with shipped quantity, shipments with tracking, amounts), without address, phone, driver position, proofs or any action; once the order is claimed by an account only `{ number, status, createdAt, claimed: true }`. 401 without a valid link, 404 for another order's number. Decision M5-30.
* `POST /api/v1/orders/{number}/cancel-requests` `{ sellerOrderNumber, reason, note? }` → `202 { requestId, status }`; idempotent (one open request per seller order). `reason`: `changed_mind | ordered_by_mistake | found_better_price | delivery_too_slow | other`.
* `POST /api/v1/orders/{number}/return-requests` `{ sellerOrderNumber, reason, comment?, lines: [{ lineId, quantity }] }` (+ optional `Idempotency-Key` header) → `202 { returnId, status }` or `422 quantity_exceeded`. `reason`: `damaged | not_as_described | wrong_item | no_longer_needed | other`.
* `POST /api/v1/orders/{number}/delivery-confirmations` `{ sellerOrderNumber }` → `202`/`200`; time is the server's, any time field is ignored.
* `POST /api/v1/orders/{number}/returns/{returnId}/label-link` → `{ labelUrl, labelUrlExpiresAt }` for an approved return.
* Shipments in the order detail now carry `id`, `timeline` (carrier events), `eta`, `etaWindowStart/End`, `driverFirstName`, `placement`, `position` (only while the stop is next and fresh) and `proofCount`. `GET /api/v1/orders/{number}/shipments/{id}/proof/{index}` returns the image (sniffed type, `nosniff`, `private, no-store`); 404 once the short-lived link has expired.

## Notifications (M5)
Buyer routes need the session and answer `private, no-store`.
* `GET /api/v1/notifications?limit=1..50&before=&beforeId=` → `{ unreadCount, notifications: [{ id, kind, category, orderNumber, sellerOrderNumber, parameters, createdAt, read }], next }`.
* `POST /api/v1/notifications/{id}/read`, `POST /api/v1/notifications/read-all` → 204, idempotent; another buyer's id is 404.
* `GET|PUT /api/v1/notifications/preferences` → `{ email: { order_updates, shipped, out_for_delivery, delivered, messages, bookings }, alwaysOn: ["refunds", "cancellations", "delivery_problems", "returns", "booking_changes"] }`; PUT sends the categories to change; unknown categories are 422, and so is naming an `alwaysOn` one with `false` ("Required for your orders and account"). Transactional mail (decision O-21, M5-21) is always sent and the `all` unsubscribe leaves it on.
* `POST /api/v1/notifications/unsubscribe` (anonymous; the signed token from a mail link; the token is `<id>.<category|all>.<expiry>.<mac>` and lives 90 days) → 204, or 400 `invalid_token`, or 400 `expired_token` (the website then points to the signed-in preferences page). The token comes in the JSON body `{ token }`, or in the `?token=` query with the RFC 8058 form body `List-Unsubscribe=One-Click` (what a mail client's unsubscribe button sends; a form POST without the marker is a 400, a GET is 405). Switches that category (or all) off, never on. Every notification mail carries `List-Unsubscribe: <{origin}/api/v1/notifications/unsubscribe?token=…>` and `List-Unsubscribe-Post: List-Unsubscribe=One-Click`.
## Passkeys (buyers)
Passkeys are WebAuthn credentials (decision M1-80). The web server relays the browser's JSON: the options it gets are `PublicKeyCredentialCreationOptions` / `PublicKeyCredentialRequestOptions` as JSON (base64url fields), the credential it returns is `PublicKeyCredential.toJSON()`. All answers are `private, no-store`. Challenges are single use, stored on the server and valid for 5 minutes.

| Endpoint | Body | Answers |
|---|---|---|
| `GET /api/v1/account/passkeys` (Bearer) | none | `200 [{ id, name, createdAt, lastUsedAt, backedUp }]` (no key material) |
| `POST /api/v1/account/passkeys/options` (Bearer) | none | `200 { challengeId, options }` for `navigator.credentials.create()`; `403 reauthentication_required` (sign in again within 10 minutes: password or passkey step-up); `409 too_many_passkeys` (10) |
| `POST /api/v1/account/passkeys` (Bearer) | `{ challengeId, name?, credential }` | `201 { id, name, createdAt, lastUsedAt, backedUp }`; `400 invalid_challenge` (unknown, used, expired, made for another session or purpose); `400 invalid_credential` (did not verify); `409 already_registered`; `409 too_many_passkeys`; `422` bad name (1 to 60 characters) |
| `PATCH /api/v1/account/passkeys/{id}` (Bearer) | `{ name }` | `204`; `404` for another buyer's or an unknown id; `422` |
| `DELETE /api/v1/account/passkeys/{id}` (Bearer) | none | `200 { token }` (every session ends, a fresh one is returned: replace the stored token, M1-87); `403 reauthentication_required`; `404`; `409 last_sign_in_method` (an account with no password keeps its last passkey) |
| `DELETE /api/v1/account/passkeys` (Bearer) | none | `200 { token }` (same rotation, M1-87): removes every passkey (one mail); `403 reauthentication_required`; `409 no_passkeys`; `409 last_sign_in_method` (no password: the passkeys are the only way in). Offer it next to the password change and sign-out-everywhere (those keep passkeys), M1-85 |
| `POST /api/v1/auth/passkey/options` | none | `200 { challengeId, options }` for `navigator.credentials.get()`: no account named, user verification required (discoverable passkeys) |
| `POST /api/v1/auth/passkey/sign-in` | `{ challengeId, credential }` | `200 { token, user: { id, displayName } }` like the password sign-in; every failure is `401 invalid_credentials` |
| `POST /api/v1/auth/reauthenticate/passkey/options` (Bearer) | none | `200 { challengeId, options }` (allowCredentials: the buyer's own passkeys); `409 no_passkeys` |
| `POST /api/v1/auth/reauthenticate/passkey` (Bearer) | `{ challengeId, credential }` | `204`: the session's `authenticated_at` is now (like `/auth/reauthenticate` with the password); `400 invalid_challenge` / `invalid_credential` |

The relying party is configured (`Auth__WebAuthnRpId`, `Auth__WebAuthnOrigins__n`): the web site must be served from one of those origins. A password reset removes every passkey of the account, and the owner gets a mail when one is added or removed. The endpoints share the strict per-client rate limit.

## Guest order links (for M3 and M5)
Not endpoints of their own: services other modules call (decision M1-70).

- `GuestLinks.Make(Guid orderId, DateTimeOffset? expiresAt = null)` returns `(token, expiresAt)`: `g1.<32 hex order id>.<expiry unix seconds>.<43 base64url MAC>`, valid for `Auth:GuestLinkDays` (30). `GuestLinks.Parse(token)` returns the order id, or null when the token is forged, edited, from another key or expired. The web server passes the token through unchanged (a path segment or header, never logged).
- `IGuestOrderClaimer.ClaimAsync(accountId, normalisedEmail, ct)` is implemented by whoever stores guest orders and registered in DI (`AddScoped<IGuestOrderClaimer, ...>`). It runs after a successful verify-email, password reset or sign-in of a verified account, with the account's address trimmed and lower-cased, and attaches the guest orders placed with that address to the account (one conditional UPDATE, idempotent).
- Configuration: `Auth__GuestLinkKey` (required in production, at least 32 characters, its own key), `Auth__GuestLinkDays`.

## Messages (M5)
Buyer routes need the session and answer `private, no-store`; another buyer's thread or message is 404.
* `GET /api/v1/messages/threads?limit=&before=&beforeId=` → `{ unreadCount, threads: [{ id, kind, subject, sellerName, orderNumber, listingId, lastMessageAt, unreadCount, preview }], next }`.
* `GET /api/v1/messages/threads/{id}?limit=&before=` → `{ thread, messages: [{ id, direction, status, sentAt, read, reported, hidden, text, attachments }], older }` (oldest first; a hidden message has no text).
* `POST /api/v1/messages` `{ threadId | orderNumber (+ sellerOrderNumber) | listingId | marketplaceBookingId, text, acknowledgeWarning? }` (`marketplaceBookingId`: a booking of the buyer's own that the portal knows; the thread has `kind: "booking"` and `marketplaceBookingId`; no receipts and no reports for booking messages, M5-31) → `202 { threadId, messageId, status }`; `409 contact_details_warning` (kinds in `errors`) until acknowledged; `422` for empty or over-long text.
* `POST /api/v1/messages/threads/{id}/read` → 204 (sends the portal read receipts once).
* `POST /api/v1/messages/{messageId}/report` `{ reason, note? }` → 202, idempotent.
* `GET /api/v1/messages/attachments/{id}` → the file as `Content-Disposition: attachment` (sniffed type, `nosniff`, CSP `default-src 'none'`, `private, no-store`); 404 unless it is the buyer's own, stored and not hidden.
`GET /api/v1/checkout/orders/{number}`: the buyer's own order (Bearer) or, for a guest, `X-Guest-Order: <guestOrderToken>` (401 bad or expired, 404 another order). After the order has been claimed by an account the guest link answers only `{ number, status, createdAt, claimed: true }` (decision M3-32).

## Demo data (test mode, M9)

Only while `Demo:Enabled` is on (only when `ASPNETCORE_ENVIRONMENT` is exactly Development, Testing or Staging, trimmed and case-insensitive: any other name, Production included, refuses to start with no override; the test site runs as `ASPNETCORE_ENVIRONMENT=Staging`). While it is off every `/api/v1/demo/*` route answers `404`, to everyone, before anything else is looked at.

| Route | Auth | Answer |
|---|---|---|
| `GET /api/v1/config` | none | `{ "demoEnabled": bool }` (public, `Cache-Control: public, max-age=60`): the web shows a TEST MODE badge when true. Always available. |
| `GET /api/v1/demo/status` | Bearer | `{ hasDemoData, counts: { addresses, carts, cartLines, orders, bookings, threads, messages, notifications } }` |
| `POST /api/v1/demo/fill` | Bearer | `201 { batchId, created: true, counts, notes[] }` the first time; `200 { created: false, ... }` with the existing batch while one exists (idempotent). `notes` says what was skipped (no catalog, a real cart already there). |
| `POST /api/v1/demo/remove` | Bearer | `200 { removed: counts }`: deletes only the demo rows of the signed-in account (marked cart lines only: a real line in the demo cart stays, and the cart becomes the buyer's own; an edited demo address is the buyer's own and stays). Fill and remove share a limit of 10 per minute per account (`429` with `Retry-After`). |
| `POST /api/v1/demo/verify` | none | `{ email, password }` -> `204`, the account is now verified. `401 invalid_credentials` for a wrong password and for an unknown address alike, `429` after too many tries (same budget as sign-in). It changes only the verification time and records `DemoVerifiedAt`: it never claims guest orders, an address that still has an unclaimed guest order is refused with the same `401`, and while `DemoVerifiedAt` is set the account never receives a guest order at all (a real mailbox proof, the verification link or a reset, clears it). A cart with any demo line answers `409 demo_data` at `POST /api/v1/checkout/quote` and `POST /api/v1/checkout`. Needed because an unverified account cannot sign in, so there is no session to take the account from. |

The account is always the signed-in buyer's, never a value from the request. What a fill creates (all marked `IsDemo` with the batch id): one address (example street, `555` phone), a cart of 2 to 3 live items (only when the buyer has no cart), three paid orders (delivered, shipped, refunded; tracking; fake `pi_demo_*` and `re_demo_*` ids), two bookings (one upcoming, one completed), one seller thread with two messages, and matching notifications. Listings whose feed document says `demo: true` are used first. Nothing is sent to the portal, a payment provider or a mailbox, and no outbox row is written. Buyer actions on a demo order, booking or thread (cancel, return, confirm delivery, message, report, reschedule) answer `409 demo_data`. Listing cards and details carry `demo: bool`.

## Wishlists and recently viewed (M6)
All routes are the signed-in buyer's own (`Authorization: Bearer`), `Cache-Control: private, no-store`; another buyer's list is 404. Money is `long` cents.
| Route | Answer |
|---|---|
| `GET /api/v1/wishlists` | `{ limit, itemLimit, wishlists: [{ id, name, itemCount, shared, sharedAt, createdAt, updatedAt }] }` |
| `POST /api/v1/wishlists` `{ name }` | `201` the list; `409 name_taken`, `422 validation_failed` (`errors.name`), `422 too_many_lists` (20) |
| `PATCH /api/v1/wishlists/{id}` `{ name }` | `200` the list; `409 name_taken` |
| `DELETE /api/v1/wishlists/{id}` | `204` |
| `GET /api/v1/wishlists/{id}` | `{ wishlist, items: [{ listingId, addedAt, unavailable, listing: {id, slug, type, title, priceFromCents, priceToCents, currency, available, brandName, sellerName, image} \| null }] }`, newest first |
| `PUT /api/v1/wishlists/{id}/items/{listingId}` | `204`, idempotent; `404` for a listing not on the storefront; `422 list_full` (200) |
| `DELETE /api/v1/wishlists/{id}/items/{listingId}` | `204` |
| `GET /api/v1/wishlists/membership?listingIds=a,b` | up to 50 ids: `{ membership: { "<listingId>": ["<listId>", ...] } }` (for the heart buttons) |
| `POST /api/v1/wishlists/{id}/share` | `{ shareKey }`, shown once; replaces any earlier link. The link is `<web>/wishlists/shared/<shareKey>` |
| `DELETE /api/v1/wishlists/{id}/share` | `204`, the link stops working |
| `GET /api/v1/shared-wishlists/{key}` | anonymous: `{ name, items: [{ listing }] }`, `404` for a wrong or revoked key; `no-store`, `noindex`, `no-referrer` |
| `GET /api/v1/recently-viewed` | `{ items: [{ viewedAt, listing }] }`, newest first, at most 20, hidden listings left out |
| `PUT /api/v1/recently-viewed/{listingId}` | `204`; the time is ours; `404` for a listing not on the storefront |
| `POST /api/v1/recently-viewed/merge` `{ listingIds }` | `204`; the guest's ids newest first (the rest of the body is ignored), never replaces a view the server has |
| `DELETE /api/v1/recently-viewed` | `204` |
Notifications (one mail per kind per listing per 7 days; demo and off-storefront listings are never announced): kinds `wishlist_price_drop` and `wishlist_back_in_stock` (`parameters`: listingId, slug, title, currency, priceCents, previousPriceCents); email category `wishlist` in `GET/PUT /notifications/preferences`.
## Staff (M7; the web's `/staff` area, never public)
Everything is under `/api/v1/staff`, bearer `stf_…` token, `private, no-store`, `noindex`, optional IP allow-list (404 outside it). Roles and permissions: [STAFF.md](STAFF.md). Mutations need `reason` (5-300 characters, no email or long number: `422 reason`) and, for sensitive ones, a passkey check within 5 minutes (`403 reauthentication_required`).

| Endpoint | Who | Notes |
|---|---|---|
| `POST /auth/enrol/options {token}` then `POST /auth/enrol {token, challengeId, name, credential}` | anonymous | The one-time link (or the bootstrap token): registers the first passkey, `201 { token, staff }` |
| `POST /auth/passkey/options` then `POST /auth/passkey/sign-in {challengeId, credential}` | anonymous | `200 { token, staff }`; every failure is `401 invalid_credentials` |
| `POST /auth/reauthenticate/options`, `POST /auth/reauthenticate`, `POST /auth/sign-out` | signed in | Step-up and sign-out |
| `GET /me`, `GET /me/passkeys`, `POST /me/passkeys/options`, `POST /me/passkeys`, `DELETE /me/passkeys/{id}` | signed in | `me` has `role`, `permissions[]`, `stepUpUntil`; adding and removing need a recent check; the last passkey stays (`409 last_passkey`) |
| `GET /accounts`, `POST /accounts {email, displayName, role, reason}` (`201 { account, enrolmentToken, expiresAt }`), `POST /accounts/{id}/enrolment {reason, revokePasskeys}`, `/role {role, reason}`, `/disable`, `/enable`, `/sign-out {reason}` | `staff.manage` | `409 last_admin` when no active admin would remain, `409 email_in_use` |
| `GET /audit?action&actor&targetType&target&limit&before` | `audit.view` | `{ entries[], next }`, newest first |
