# Marketplace API reference

All errors are `{ "code": string, "message": string }`. Another user's resource is `404`, never `403`.
Rate limited endpoints answer `429 { code: "rate_limited" }` with `Retry-After`.

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/health` | none | Liveness: `{ "status": "ok" }` |
| GET | `/api/v1/ping` | none | Public rate limit bucket smoke endpoint (removed when the catalog endpoints land) |

## Headers the web server (BFF) must send
The API sees every buyer request coming from the web server, so rate limits need the end user's address, forwarded like this on **every** call:

| Header | Value |
|---|---|
| `X-Solvelt-Client-Ip` | The end user's IP literal (IPv4 or IPv6) as seen by the web server |
| `X-Solvelt-Client-Timestamp` | Unix seconds, web server clock (accepted within 300 s) |
| `X-Solvelt-Client-Signature` | `v1=` + lower-case hex `HMAC-SHA256(Web:ForwardingKey, "<timestamp>\n<ip>")` |

`Web:ForwardingKey` (env `Web__ForwardingKey`) is a dedicated key shared only by the web server and this API; it is never a portal secret and never reaches the browser.
Headers that are missing, malformed, stale or wrongly signed are ignored (the request is then limited together with every other unsigned request from the same peer), so always send all three.
`Authorization: Bearer <session token>` is added by the auth slice. Every response may carry `X-Correlation-Id` on a `500`.

