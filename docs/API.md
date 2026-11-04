# Marketplace API reference

All errors are `{ "code": string, "message": string }`. Another user's resource is `404`, never `403`.
Rate limited endpoints answer `429 { code: "rate_limited" }` with `Retry-After`.

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | none | Liveness: `{ "status": "ok", "forwarding": { "invalid": <count since start> } }` |
| POST | `/api/portal/webhooks` | portal signature | Portal -> marketplace events; see below |
| GET | `/api/v1/catalog/categories` | none | Category tree with visible-listing counts; see Catalog |
| GET | `/api/v1/catalog/brands` | none | Brands that have visible listings |
| GET | `/api/v1/catalog/listings` | none | Paged, filterable, sortable listing cards |
| GET | `/api/v1/catalog/brands/{slugOrId}` | none | Brand storefront: seller, count, category tree |
| GET | `/api/v1/catalog/sellers/{id}` | none | Seller storefront: brands and category tree |
| GET | `/api/v1/catalog/sitemap/{kind}` | none | Paged keys + last-modified for sitemaps (`listings`, `categories`, `brands`, `sellers`) |
| GET | `/api/v1/catalog/suggest` | none | Autocomplete: listing, brand and category suggestions |
| GET | `/api/v1/catalog/listings/{slugOrId}` | none | One listing in full |
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
`Authorization: Bearer <session token>` is added by the auth slice. Every response may carry `X-Correlation-Id` on a `500`.

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
Only **visible** listings are ever returned (CONTRACT.md §2): last known portal status `Active`, seller active, and not unlisted. A listing that is not visible, or does not exist, is a `404 not_found`, with no hint which, with one exception: **a listing that was public and has been unlisted, archived or removed answers `410 { "code": "gone" }`** (by slug or id), so search engines drop it quickly. A suspended seller's listings, drafts, listings we only heard an event about, and unknown ids stay `404`. Slugs never change (M1-32), so there is nothing to redirect except `/…/{id}` to the slug: the detail response by id carries the canonical `slug`.
**Unlisting is immediate**: a `listing.unlisted` portal event hides a listing at once, so do not cache catalog responses for longer than a few seconds.
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
| `q` | Search text (at most 100 characters, else `400`). Normalised to lower-case ASCII words (accents folded, punctuation ignored), at most 8 words of 40 letters; **every** word must match and the last one matches as a prefix. Matches title, brand, the seller's search terms and the description, **weighted in that order**; an exact word beats a prefix. When nothing matches, a typo-tolerant pass runs (words of 4+ letters, about one slip in six letters; every word must be close). Combines with every other filter. Blank or missing = no search; punctuation only = no results. At most 1000 best matches are ranked, so `total` never exceeds 1000 for a search |
| `priceMin`, `priceMax` | Whole cents, 0 to 1000000000000; `priceMin` above `priceMax` is `400`. A listing matches when some buyable price lies in the range (its price range overlaps it) |
| `available` | `true` leaves out sold-out listings (anything else is `400`) |
| `kosher` | Comma separated kosher certifications (`OU,Star-K`): alternatives (OR). Shortcut for `attr=kosher-certification:<value>` |
| `delivery` | Comma separated, each one of `ships`, `local`, `at-home`, `online`: alternatives. Shortcut for `attr=delivery:<value>`; products carry `ships` and sometimes `local`, services `at-home` and/or `online` |
| `attr` | Repeatable `key:value` filter on any attribute (`attr=kosher-status:dairy`). Values of one key are alternatives (OR), different keys all have to hold (AND). At most 12 pairs, 10 values per key, keys up to 100 and values up to 60 characters, else `400` |
| `facets` | `true` adds a `facets` object (see below); anything but `true`/`false` is `400` |
| `sort` | `relevance` (default when `q` is given; without `q` it behaves as `newest`), `newest` (default otherwise), `best_selling` (placeholder: the newest order until M3 records sales), `price_asc`, `price_desc`, `title`. Ties break on id, so paging never repeats or drops an item |
| `cursor` | Opaque `nextCursor` of the previous page, for the same filters and sort. Use it instead of `page` (both is `400`; a cursor of another sort or a forged one is `400`). A cursor is a position, so new listings arriving between two requests never shift or repeat items |

Every parameter except `attr` may be given once; a repeat is `400`.
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
`nextCursor` is `null` on the last page (the API fetches one extra row to know). With `cursor`, `page` is `0`; `total` and `totalPages` are still given. Search is capped at 1000 matches (`total` never exceeds it).

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
`kind` is `listings` (key = slug, last modified = the portal's updated time), `categories` and `brands` (key = slug; last modified = the newest update among their visible listings, a category counts its whole subtree) or `sellers` (key = id; same rule). Only entries with at least one visible listing appear, so an unlisted listing leaves at once. `pageSize` 1 to 5000 (default 1000), ordered by id so pages are stable; anything else `400`, an unknown kind `404`.
```json
{ "kind": "listings", "page": 1, "pageSize": 1000, "total": 40, "totalPages": 1, "items": [ { "key": "classic-braided-challah", "lastModified": "2026-09-04T09:00:00+00:00" } ] }
```
The web tier builds `sitemap.xml` (an index of `totalPages` child sitemaps per kind) from this; it must not cache the answers for more than a few seconds beyond what its own sitemap cache needs.

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
For operators and the future staff area (M7), not for browsers. Send `X-Solvelt-Admin-Key: <Admin:ApiKey>` (env `Admin__ApiKey`, at least 32 characters, its own key). Without a configured key the endpoints answer `404`; a missing or wrong key is `401 { code: "unauthorized" }`. Rate limited like the auth endpoints.

### `GET /api/admin/outbox?status=pending|delivered|dead&limit=1..200`
`200 { counts: { pending, delivered, dead }, messages: [{ id, type, status, attempts, nextAttemptAt, lastError, createdAt, deliveredAt, deadAt }] }`, newest first (default 50). `lastError` is a short code (for example `portal_422_exceeds_sale`, `email_not_configured`, an exception type). The payload is never returned. An unknown `status` is `400 invalid_status`.

### `POST /api/admin/outbox/{id}/retry`
`204`: a dead message goes back to pending with 0 attempts and is due now. `409 not_retryable`: the message is not dead, or its payload was already purged (30 days after dead-lettering). `404`: unknown id.

### Delivery rules
At least once. A failed message is retried after 30 s, 60 s, 120 s ... doubling, capped at 1 h, and is dead after 8 failures (or at once when waiting cannot help, such as a portal `422`). Configuration: `Outbox:MaxAttempts`, `BackoffSeconds`, `MaxBackoffSeconds`, `LeaseSeconds`, `BatchSize`, `PollSeconds`, `DeliveredPayloadRetentionDays`, `DeadPayloadRetentionDays`, `RowRetentionDays`.
