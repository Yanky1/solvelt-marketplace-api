# Marketplace API reference

All errors are `{ "code": string, "message": string }`. Another user's resource is `404`, never `403`.
Rate limited endpoints answer `429 { code: "rate_limited" }` with `Retry-After`.

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | none | Liveness: `{ "status": "ok", "forwarding": { "invalid": <count since start> } }` |
| POST | `/api/portal/webhooks` | portal signature | Portal -> marketplace events; see below |
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

