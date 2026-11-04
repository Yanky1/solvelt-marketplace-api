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
| `q` | Search text (at most 100 characters, else `400`). Split into words of Unicode letters and digits, lower-cased, with accents, Hebrew niqqud and cantillation dropped (pointed and unpointed Hebrew are the same word), Latin accents folded ("Crème" is "creme"), full-width forms and ligatures folded, a maqaf (־) acting as a word break and a geresh/gershayim/apostrophe not; at most 8 words of 40 characters. **Every** word must match and the last one matches as a prefix. Matches title, brand, the seller's search terms and the description, **weighted in that order**; an exact word beats a prefix. Chinese and Japanese have no spaces, so a run of characters is one word and matches by its beginning. When nothing matches, a typo-tolerant pass runs (words of 4+ characters; PostgreSQL uses trigram word similarity, the SQLite test fallback edit distance, so borderline typos can differ between them). Combines with every other filter, facet and sort: they apply to **all** matches (at most 50 000 per query, a safety bound: a query matching more is cut to its 50 000 most relevant). Blank or missing = no search; punctuation only = no results |
| `priceMin`, `priceMax` | Whole cents, 0 to 1000000000000; `priceMin` above `priceMax` is `400`. A listing matches when some buyable price lies in the range (its price range overlaps it) |
| `available` | `true` leaves out sold-out listings (anything else is `400`) |
| `kosher` | Comma separated kosher certifications (`OU,Star-K`): alternatives (OR). Shortcut for `attr=kosher-certification:<value>` |
| `delivery` | Comma separated, each one of `ships`, `local`, `at-home`, `online`: alternatives. Shortcut for `attr=delivery:<value>`; products carry `ships` and sometimes `local`, services `at-home` and/or `online` |
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

## Auth (buyers)
All bodies are JSON. The web server holds the session token in its own HttpOnly cookie and sends `Authorization: Bearer <token>`. Auth endpoints share the strict per-client-IP rate limit (`RateLimits:AuthPerMinute`, default 10), forwarded as described above. This is the contract the web app assumed (its W1-21), confirmed.

| Endpoint | Body | Answers |
|---|---|---|
| `POST /api/v1/auth/sign-up` | `{ email, password, displayName }` | `204` for a new, an unverified and an already registered address (same empty body); `422 { code: "validation_failed", errors: { email?, password?, displayName? } }` |
| `POST /api/v1/auth/verify-email` | `{ token, password }` | `204`, or `400 { code: "invalid_token" }` (unknown, used, expired, wrong kind, **or a wrong password**: the link is then not used up); `422` on `password` when it is missing. The password is the one chosen at sign-up (decision O-16). Someone who does not know it uses forgot-password |
| `POST /api/v1/auth/resend-verification` | `{ email }` | `204` always; a new link replaces older ones; at most 3 mails an hour per address |
| `POST /api/v1/auth/sign-in` | `{ email, password }` | `200 { token, user: { id, displayName } }`; `401 invalid_credentials` (wrong password and unknown address are identical); `403 email_not_verified` (only after the correct password); `429 rate_limited` with `Retry-After` (the address is delayed or the client's hourly failure budget is spent; the same for a known and an unknown address) |
| `POST /api/v1/auth/sign-out` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/sign-out-all` | none | `204`; `401` without a valid token |
| `POST /api/v1/auth/forgot-password` | `{ email }` | `204` always; mail for any existing account, verified or not; the same database work for an unknown address |
| `POST /api/v1/auth/reset-password` | `{ token, password }` | `204`, or `400 invalid_token`, or `422` for a weak password or one equal to the address (the link is not used up); every session of the account ends; the address is marked verified and its sign-in delay is cleared |
| `POST /api/v1/auth/reauthenticate` | `{ password }` (Bearer) | Step-up: `204` and the session's `authenticated_at` is now; `401 invalid_credentials`; `429` with `Retry-After`; `422` when the password is missing. Same gate as sign-in |
| `POST /api/v1/auth/session/rotate` | none (Bearer) | `200 { token }`; the old token is revoked; the absolute expiry and `authenticated_at` carry over; `401` when the session is gone |
| `GET /api/v1/account/me` | none | `200 { id, displayName, email }`, or `401 { code: "unauthorized" }` |

Rules: email is trimmed and lower-cased; password 10 to 128 characters and not the email address; display name 1 to 60 characters. The token returned by sign-in is 43 characters of base64url, shown once. Sessions end after 7 idle days (sliding) or 30 days in all. Sign-in is **delayed, not locked** (decision M1-63): six failures per address with no delay, then the next attempt must wait 1 s, 2 s, 4 s ... up to 15 minutes, the same for a known and an unknown address; a right password or a password reset clears it; each client IP may fail 20 times an hour across all addresses. Auth request bodies are limited to 16 KB (`413`). Links in mails open `<Marketplace:PublicBaseUrl>/verify-email?token=...` and `/reset-password?token=...` (24 h and 60 min; the token is `<32 hex>.<43 base64url>` and is opaque to the web: pass it through unchanged); the pages must POST the token (a mail scanner opening the link then cannot use it up). The buyer is known to the portal only as a relay address and a name like "Jane B." (decision M1-55 and the PII table).

## Account (buyers)
All endpoints need `Authorization: Bearer <token>` (`401 unauthorized` otherwise) and answer `Cache-Control: no-store, private`.

| Endpoint | Body | Answers |
|---|---|---|
| `GET /api/v1/account/profile` | none | `200 { id, displayName, email, phone }` |
| `PUT /api/v1/account/profile` | `{ displayName, phone? }` | `200` the profile; `422 validation_failed` with `errors` per field |
| `GET /api/v1/account/addresses` | none | `200 [address]`, default first then oldest |
| `POST /api/v1/account/addresses` | `{ fullName, line1, line2?, city, region?, postalCode, country, phone?, lat?, lng?, isDefault? }` | `201` the address; `422`; `409 too_many_addresses` (20); the first address is always the default |
| `GET/PUT/DELETE /api/v1/account/addresses/{id}` | as above for PUT | `200`/`204`; `404` for another buyer's, unknown or malformed ids; deleting the default promotes the oldest remaining |
| `POST /api/v1/account/addresses/{id}/default` | none | `200` the address, now the only default |
| `POST /api/v1/account/password` | `{ currentPassword, newPassword }` | `204`; `403 reauthentication_required` (sign in again within 10 minutes: call `/auth/reauthenticate`, then repeat); `401 invalid_credentials`; `422` weak password; `429` with `Retry-After`; other sessions are revoked |
| `POST /api/v1/account/delete` | `{ password }` | `204`; same errors as the password change; the session and every other one stop at once, personal data is erased by a job shortly after |

US addresses (`country: "US"`) need a 5 or 9 digit ZIP and a state code; other countries a plain postal code. `lat` and `lng` come together or not at all.

## Cart (M3)
`/api/v1/cart`, public rate limit. The owner is the signed-in buyer (`Authorization: Bearer`) or a guest (`X-Solvelt-Cart-Token`). A guest cart is created by the first add; the answer carries `cartToken` **once** (43 base64url characters; the web server keeps it in its own HttpOnly cookie). An unknown guest token never becomes a cart id: it gets a fresh cart and a fresh token. A present but invalid Bearer is `401` (never a silent guest cart).

| Endpoint | Body | Answers |
|---|---|---|
| `GET /api/v1/cart` | none | `200 CartView` (an empty cart when none exists) |
| `POST /api/v1/cart/lines` | `{ variantId, quantity? = 1 }` | `200 CartView`; `404` hidden, unlisted, suspended-seller or unknown variant; `422 not_purchasable` (service or sold out), `quantity_limit` (1..20), `cart_full` (50 lines), `currency_mismatch`. Adding a variant already in the cart adds to its line (capped at 20) |
| `PATCH /api/v1/cart/lines/{lineId}` | `{ quantity }` (0 removes) | `200 CartView`; `404` for a line that is not in this cart; `422 quantity_limit` |
| `DELETE /api/v1/cart/lines/{lineId}` | none | `200 CartView`; `404` |
| `POST /api/v1/cart/merge` | `{ guestToken }`, Bearer required | `200 CartView`: moves the guest cart into the buyer's (quantities add, capped at 20). Call it right after sign-in; repeating it or an unknown token changes nothing |

`CartView = { currency, groups: [{ sellerId, sellerName, brandId, brandName, subtotalCents, lines: [{ id, listingId, variantId, slug, title, variantName, imageId, quantity, unitPriceCents, compareAtCents, lineTotalCents, status: ok|unavailable, previousUnitPriceCents }] }], itemCount, subtotalCents, canCheckout, cartToken }`. Groups are per (seller, brand), exactly as the portal splits seller orders. Everything is read from the catalog on every call: a client-supplied price, total or name is not an input anywhere. `unavailable` lines (hidden, unlisted, seller inactive, sold out, gone) count as 0 and make `canCheckout` false until removed. `previousUnitPriceCents` is set when the price differs from the one the buyer last saw (changing the quantity accepts the new price).

## Checkout quote (M3)
`POST /api/v1/checkout/quote`: prices the cart for an address and the delivery choices made so far. Same owner rules as the cart (Bearer, or `X-Solvelt-Cart-Token`). Safe to repeat; reserves nothing. **The body has no money field and none is read.**

Body: `{ address: { country (2 letters), region? (state, needed for US tax), postalCode, lat?, lng? }, selections?: [{ sellerId, brandId, speed? | localWindowId? + localDate? }] }`. A group with no selection gets its cheapest carrier speed. A selection names a speed the portal offers, **or** a local window from the quote's own `local.windows` (not both).

`200 { currency, groups: [{ sellerId, sellerName, brandId, brandName, lines: [{ lineId, variantId, title, quantity, unitPriceCents, lineTotalCents, taxCents }], itemsCents, shippingOptions: [{ speed, priceCents, free, estimate: { earliest, latest } }], local: { eligible, reason, feeCents, timeZone, windows: [{ windowId, date, start, end, remaining }] } | null, selected: { kind: carrier|local, speed?, windowId?, date?, shippingCents } | null, shippingCents, shippingTaxCents, taxCents, totalCents }], itemsCents, shippingCents, taxCents, totalCents, canPay }`. `canPay` is false when a group has nothing to ship with (for example a destination outside every zone). Every total is the exact sum of its parts.
Errors: `401`, `422 validation_failed` (address), `422 invalid_selection` (`detail` in `message`: unknown group, speed not offered, window not offered or full, speed mixed with a window), `409 cart_not_ready` (empty cart or an unavailable line), `503 shipping_unavailable` (the portal's settings could not be read; try again).

## Checkout and payment (M3)
`POST /api/v1/checkout` (Bearer required; guest checkout comes with M1's order access links). Body: `{ address: { name, line1, line2?, city, region?, postalCode, country, phone?, lat?, lng? }, selections?: [...as in the quote], giftMessage? (<= 500) }`. **No money field exists; none is read.** It prices the cart exactly like the quote, then:
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
* `charge.dispute.*`: forwarded to the portal as `dispute.created|updated|closed` (with `createdAt` = Stripe's event time), one per seller order of the payment (amount = the seller share of the dispute, split exactly). Older events and anything after `closed` are dropped. A dispute spanning several seller orders is HELD in the outbox until `Portal:PerSellerOrderDisputes` is turned on (portal request C10, M3-19); single-seller disputes go out at once.
Portal webhooks (`/api/portal/webhooks`) now also act on `refund.issued` and `order.cancelled` (with `refundDueCents`): the buyer is refunded through Stripe EXACTLY the amount the portal states (it already includes line tax and shipping) and the portal is told with `refund.processed`, whose `amountCents` excludes the tax (backed out per line, floor proration; decision M3-16).

## Buyer orders (M5)
All routes need the buyer session (`Authorization: Bearer`), answer `Cache-Control: private, no-store`, and answer 404 for another buyer's order.
* `GET /api/v1/orders?limit=1..50&before=<createdAt>` → `{ orders: [{ number, createdAt, totalCents, refundedCents, groups: [{ sellerOrderNumber, sellerName, status, itemCount, firstItem }] }], next }`.
* `GET /api/v1/orders/{number}` → the order, per seller order: `status` (`processing | new | accepted | on_hold | partially_shipped | shipped | delivered | cancelled | refunded`), lines with `shippedQty/returnableQty/...`, `shipments`, `returns` (with a live `label` only), `cancelRequest`, `refunds` (shown, never moved here), and `canCancel / canReturn / canConfirmDelivery`.
* `POST /api/v1/orders/{number}/cancel-requests` `{ sellerOrderNumber, reason, note? }` → `202 { requestId, status }`; idempotent (one open request per seller order). `reason`: `changed_mind | ordered_by_mistake | found_better_price | delivery_too_slow | other`.
* `POST /api/v1/orders/{number}/return-requests` `{ sellerOrderNumber, reason, comment?, lines: [{ lineId, quantity }] }` (+ optional `Idempotency-Key` header) → `202 { returnId, status }` or `422 quantity_exceeded`. `reason`: `damaged | not_as_described | wrong_item | no_longer_needed | other`.
* `POST /api/v1/orders/{number}/delivery-confirmations` `{ sellerOrderNumber }` → `202`/`200`; time is the server's, any time field is ignored.
* `POST /api/v1/orders/{number}/returns/{returnId}/label-link` → `{ labelUrl, labelUrlExpiresAt }` for an approved return.

## Guest order links (for M3 and M5)
Not endpoints of their own: services other modules call (decision M1-70).

- `GuestLinks.Make(Guid orderId, DateTimeOffset? expiresAt = null)` returns `(token, expiresAt)`: `g1.<32 hex order id>.<expiry unix seconds>.<43 base64url MAC>`, valid for `Auth:GuestLinkDays` (30). `GuestLinks.Parse(token)` returns the order id, or null when the token is forged, edited, from another key or expired. The web server passes the token through unchanged (a path segment or header, never logged).
- `IGuestOrderClaimer.ClaimAsync(accountId, normalisedEmail, ct)` is implemented by whoever stores guest orders and registered in DI (`AddScoped<IGuestOrderClaimer, ...>`). It runs after a successful verify-email, password reset or sign-in of a verified account, with the account's address trimmed and lower-cased, and attaches the guest orders placed with that address to the account (one conditional UPDATE, idempotent).
- Configuration: `Auth__GuestLinkKey` (required in production, at least 32 characters, its own key), `Auth__GuestLinkDays`.
