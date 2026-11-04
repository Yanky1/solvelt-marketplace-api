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
| GET | `/api/v1/catalog/suggest` | none | Autocomplete: listing, brand and category suggestions |
| GET | `/api/v1/catalog/listings/{slugOrId}` | none | One listing in full |
| POST | `/api/v1/auth/sign-up` | none | Create an account; `204` always for a valid body; see Auth |
| POST | `/api/v1/auth/resend-verification` | none | `204` always |
| POST | `/api/v1/auth/verify-email` | none | `204` or `400 invalid_token` |
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
Only **visible** listings are ever returned (CONTRACT.md §2): last known portal status `Active`, seller active, and not unlisted. A listing that is not visible, or does not exist, is a `404 not_found`, with no hint which.
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
| `sort` | `relevance` (default when `q` is given; without `q` it behaves as `newest`), `newest` (default otherwise), `price_asc`, `price_desc`, `title`. Ties break on id, so paging never repeats or drops an item |
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
  "page": 1, "pageSize": 24, "total": 40, "totalPages": 2 }
```
`priceFromCents`/`priceToCents` are the range over the variants that can be bought now (over all variants when everything is sold out); `compareAtFromCents` is the lowest struck-through price above the price, or `null`; `available` is false when every variant is sold out (the listing is still shown); `image` is `null` when there is none.

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

## Auth (buyers)
All bodies are JSON. The web server holds the session token in its own HttpOnly cookie and sends `Authorization: Bearer <token>`. Auth endpoints share the strict per-client-IP rate limit (`RateLimits:AuthPerMinute`, default 10), forwarded as described above. This is the contract the web app assumed (its W1-21), confirmed.

| Endpoint | Body | Answers |
|---|---|---|
| `POST /api/v1/auth/sign-up` | `{ email, password, displayName }` | `204` for a new, an unverified and an already registered address (same empty body); `422 { code: "validation_failed", errors: { email?, password?, displayName? } }` |
| `POST /api/v1/auth/verify-email` | `{ token }` | `204`, or `400 { code: "invalid_token" }` (unknown, used, expired, wrong kind) |
| `POST /api/v1/auth/resend-verification` | `{ email }` | `204` always; a new link replaces older ones; at most 3 mails an hour per address |
| `POST /api/v1/auth/sign-in` | `{ email, password }` | `200 { token, user: { id, displayName } }`; `401 invalid_credentials` (wrong password and unknown address are identical); `403 email_not_verified` (only after the correct password); `429 rate_limited` with `Retry-After` (locked, or too many attempts) |
| `POST /api/v1/auth/sign-out` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/sign-out-all` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/forgot-password` | `{ email }` | `204` always; mail only for a verified account |
| `POST /api/v1/auth/reset-password` | `{ token, password }` | `204`, or `400 invalid_token`, or `422` for a weak password (the link is not used up); every session of the account ends |
| `GET /api/v1/account/me` | none | `200 { id, displayName, email }`, or `401 { code: "unauthorized" }` |

Rules: email is trimmed and lower-cased; password 10 to 128 characters and not the email address; display name 1 to 60 characters. The token returned by sign-in is 43 characters of base64url, shown once. Sessions end after 7 idle days (sliding) or 30 days in all. Five wrong passwords lock the account for 15 minutes. Links in mails open `<Marketplace:PublicBaseUrl>/verify-email?token=...` and `/reset-password?token=...` (24 h and 60 min); the pages must POST the token (a mail scanner opening the link then cannot use it up). The buyer is known to the portal only as a relay address and a name like "Jane B." (decision M1-47 and the PII table).
