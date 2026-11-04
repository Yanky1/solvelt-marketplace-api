# Decisions (marketplace API)

Orchestrator decisions use ids `O-nn`; workstream decisions use `M1-nn`, `M2-nn`, ...

| Id | Date | Decision | Why |
|---|---|---|---|
| O-01 | 2026-11-04 | Catalog feed is missing in the portal (only on its ROADMAP). Requested as contract change C1; M1 builds the read model behind `ICatalogSource` with a fixture source until it lands. | Unblocks M1/M2 without waiting on the portal. |
| O-02 | 2026-11-04 | Buyer sessions: opaque tokens stored hashed in the API; the SSR web server keeps them in its own HttpOnly cookie and calls the API with a bearer token (BFF). | SSR loaders run on the web server; no tokens in browser JS. |
| O-03 | 2026-11-04 | One webhook endpoint `/api/portal/webhooks` for every event family; idempotency key `X-Solvelt-Event-Id`. Confirmation requested as C3. | The portal has one `Orders:WebhookUrl`. |
| O-04 | 2026-11-04 | M2/M3 start after M1's portal client, receiver and catalog read model merge; W1 (web foundation) starts in parallel once M1's API skeleton exists. | Avoids rebase churn on an empty repo. |
| O-05 | 2026-11-04 | `.claude/settings.json` is NOT committed by the orchestrator: writing permission settings was refused as self-modification. The repo owner adds it (same content as the portal repos). | Permission changes belong to the owner. |
