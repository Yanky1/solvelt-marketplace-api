# Deploying the buyer marketplace (API and website)

This page is for the person who sets up a deployment (test site or production). It covers both apps: this API (ASP.NET Core 10) and the website (Yanky1/solvelt-marketplace-web, a Node server). Anything this page cannot know is marked **owner to decide**. Where a setting is not listed here it has a safe default (see the README's configuration table for every tuning knob and its default).

Settings are environment variables. On the API, `__` stands for a `:` level (`Auth__EmailLinkKey` is `Auth:EmailLinkKey`). On the website they are plain names (`SESSION_COOKIE_SECRET`).

**Before you start, generate every secret separately**, for example with `openssl rand -base64 48`. Never reuse one value in two places: both apps refuse to start when two keys are equal (the one deliberate exception is the forwarding key, which must be the SAME on both apps). Never put a secret in a file in either repository.

## 1. The picture

```
buyer's browser ──https──> website (Node, PORT 3000)  ──server-to-server──> API (ASP.NET Core)
                                                                                 ▲
Stripe ───────────── https: POST /api/stripe/webhooks ───────────────────────────┤
seller portal ────── https: POST /api/portal/webhooks ───────────────────────────┤
buyer's mail app ─── https: GET  /api/v1/notifications/unsubscribe…, /api/v1/auth/phone-email ┤
SignalWire (voice, optional) ─ https + wss: /api/v1/voice/… ──────────────────────────────┤
Sola (phone payments, optional) ─ https: POST /api/v1/voice/sola/webhook ───────────────────┘

Phone card payments only (section 8), a THIRD public host, its own service and secret store:
SignalWire <Pay> ──https + Basic auth: POST /connector──> pay connector (src/Solvelt.PayConnector) ──https──> Sola gateway
API ─────────────── https, signed: POST /charge-token ──> pay connector
pay connector ───── https, signed: POST /api/v1/voice/pay-connector/{expect,result,card} ──> API
```

- The browser talks ONLY to the website. The website calls the API from the server (it is a "backend for frontend"): every page and form goes through it, and the website never proxies `/api` for the browser.
- A few callers must reach the API **directly** over https: Stripe's webhook, the seller portal's webhook, links in mail (one-click unsubscribe, the "confirm this email" page of an account made by phone) and, if voice is on, SignalWire, Sola's webhook and the pay connector. So the API needs a public https address for those paths, even though everything else may be restricted to the website.
- **How `/api` sits next to the website's origin: owner to decide.** Two ways work with the code as it is:
  1. **Two host names** (recommended, simplest): `https://www.<domain>` for the website and `https://api.<domain>` for the API. Set `Notifications__PublicApiBaseUrl=https://api.<domain>` and give Stripe and the portal `https://api.<domain>/api/...`.
  2. **One host name, path routing at the reverse proxy**: `https://www.<domain>/api/stripe/webhooks`, `/api/portal/webhooks`, `/api/v1/notifications/unsubscribe`, `/api/v1/auth/phone-email` and `/api/v1/voice/` (including its WebSocket stream) go to the API; everything else goes to the website. Then `Notifications__PublicApiBaseUrl=https://www.<domain>`. Do NOT route `/api/admin` or `/api/v1/staff` publicly (see section 6).

  With phone card payments on, the **pay connector is a third public host** (for example `https://pay.<domain>`), never behind the website and never path-routed through the API's host: it is its own cardholder-data environment (docs/VOICE.md section 6). Only SignalWire (`POST /connector`, signature plus Basic auth) and the API (`POST /charge-token`, signed) call it; it calls the API's `/api/v1/voice/pay-connector/…` paths and Sola. Its `Connector__PublicUrl` is exactly the URL SignalWire calls, and the API's `Sola__PayConnectorUrl` points at the same origin. Its `/health` is fine for the platform's health check.

  Either way, the website reaches the API through `API_BASE_URL` (an internal address is fine, for example an App Service private endpoint).

## 2. TLS and the reverse proxy

- Both apps run behind a TLS-terminating proxy (Azure App Service today) and see plain http. Neither trusts `X-Forwarded-Proto`, `X-Forwarded-Host` or `Forwarded` (decisions M8-13, M8W-04): every public address comes from configuration (`PUBLIC_ORIGIN` on the website; `Marketplace__PublicBaseUrl`, `Notifications__PublicApiBaseUrl` and the passkey origins on the API).
- Turn on **HTTPS Only** for both apps (Azure App Service setting). The API has no HTTPS redirect or HSTS of its own.
- Restrict the API to the website for everything except the public paths in section 1 (Azure access restrictions or a private endpoint plus the proxy rule above). **The exact network setup: owner to decide.**
- `TRUSTED_PROXIES` on the website: the addresses or CIDR ranges of the proxy in front of it, so the buyer's real IP is read from `X-Forwarded-For` only when it comes from there. The website then signs that IP for the API with the forwarding key.
- Certificates: **owner to decide** (Azure-managed certificates, or your own).
- Keep both servers' clocks in NTP sync: the API accepts the website's signed client-IP header only within a short window.

## 3. The API: settings

"Secret" means: generate it, keep it out of logs and repositories, rotate it if it leaks. Every API secret below must be **at least 32 characters**, must not be a development default, and must **differ from every other key** (start-up checks all of this outside Development/Testing).

### Required in production

| Setting | Secret | Rules | What it is |
|---|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | no | `Production` (a test site: `Staging`) | Anything but Development/Testing gets every production check |
| `Database__Provider` | no | `Postgres` | SQLite is for development and tests only |
| `ConnectionStrings__Default` | **yes** (it holds the password) | a PostgreSQL connection string | See section 5 |
| `Portal__BaseUrl` | no | https URL | The seller portal's API |
| `Portal__InboundSecret` | **yes** | 32+, differs from every key | Signs our calls to the portal; the portal must hold the same value (**owner to set on the portal**) |
| `Portal__WebhookSecret` | **yes** | 32+, differs from every key | Verifies the portal's webhooks; the portal must hold the same value |
| `Web__ForwardingKey` | **yes** | 32+, differs from every other API key, **must EQUAL the website's `WEB_FORWARDING_KEY`** | Verifies the client IP the website forwards |
| `Auth__EmailLinkKey` | **yes** | 32+, differs from every key | Signs verify and reset links in account mail |
| `Auth__GuestLinkKey` | **yes** | 32+, differs from every key | Signs guest order-access links |
| `Auth__WebAuthnRpId` | no | the website's registrable domain, no scheme or port (for example `solvelt.com`) | Passkeys are bound to it **forever**: changing it later orphans every registered passkey. Decide it once |
| `Auth__WebAuthnOrigins__0` (`__1` ...) | no | the website's exact https origin(s), on that domain | Where passkeys may be used |
| `Checkout__QuoteKey` | **yes** | 32+, differs from every key | Binds a checkout to its quote |
| `Notifications__UnsubscribeKey` | **yes** | 32+, differs from every key | Signs unsubscribe links |
| `Notifications__PublicApiBaseUrl` | no | https URL | The public address of the API (section 1) |
| `Marketplace__PublicBaseUrl` | no | https URL | The WEBSITE's address: every link in mail starts with it |
| `Marketplace__RelayDomain` | no | a domain you control | Buyers' relay addresses (`relay-…@<domain>`), the only addresses sellers see. **Which domain, and how mail to it is handled: owner to decide** |
| `Email__Provider` | no | `Smtp` | Without mail nobody can verify an address or reset a password, so the API does not start without it |
| `Email__SmtpHost`, `Email__From` | no | a host; an email address | **SMTP provider: owner to decide** |
| `Email__SmtpUserName`, `Email__SmtpPassword` | **password: yes** | as the provider says | Optional if the provider needs none; port 587 and TLS by default (`Email__SmtpPort`, `Email__SmtpUseTls`) |

### Optional

| Setting | Secret | Rules | What it is |
|---|---|---|---|
| `Stripe__SecretKey` | **yes** | **test keys only**: `sk_test_…` or `rk_test_…`. A live key (`_live_`) stops the API in every environment | Without it payments use an in-process fake (development only) |
| `Stripe__PublishableKey` | no (public by design) | `pk_test_…` | Shown to the browser for the Payment Element; a warning at start when the secret key is set without it |
| `Stripe__WebhookSecret` | **yes** | `whsec_…`, **required when `Stripe__SecretKey` is set**, differs from every key | Verifies Stripe's webhooks (section 7) |
| `Auth__PhoneCodeKey` | **yes — a NEW owner secret (W3-02)** | 32+, differs from every key AND from `Voice__PhoneCodeKey`, `Voice__StreamTokenKey`, `Voice__CallerHashKey`, `SignalWire__SigningKey`, `SignalWire__ApiToken` | Signs the SMS codes the website sends to accounts made by phone. **Unset turns web sign-in by phone off.** It needs the SignalWire messaging settings to deliver the texts |
| `Portal__ImageKey` | **yes** | 32+, differs from every key; required when `Portal__RequireImageKey=true` | Signs image requests to the portal |
| `Portal__PublicBaseUrl` | no | https URL; default `Portal__BaseUrl` | The only origin a return-label link may have |
| `Admin__ApiKey` | **yes** | 32+, differs from every key; needs `Admin__Host` | Turns on `/api/admin` (outbox list and retry). Unset: off |
| `Admin__Host` | no | a host name that is NOT the public one | The only host `/api/admin` answers on |
| `Staff__BootstrapToken`, `Staff__BootstrapEmail`, `Staff__BootstrapName` | **token: yes** | token 32+, differs from every key; email required with it | One-time creation of the first staff admin (section 10); remove afterwards |
| `Staff__AllowedIps__0` ... | no | addresses or CIDRs | Who may reach the staff API; unset allows all |
| `Demo__Enabled` | no | `true` only with `ASPNETCORE_ENVIRONMENT` Development, Testing or Staging | Test mode (demo data). A test site's mail must go to a sandbox |
| `Voice__Enabled` and the `Voice__*`, `SignalWire__*`, `Sola__*` settings | several **yes** | section 8 and docs/VOICE.md | Ordering and paying by phone. Off by default |
| `Database__MigrateOnStart` | no | default `true` | `false` to run migrations from a pipeline instead |
| Tuning knobs (`Auth__*` durations, `Outbox__*`, `Portal__*` timeouts, `Catalog__*`, `RateLimits__*`, `Staff__*`, `Services__*`, `Bookings__*`, `Shipping__*`, `Tax__*`) | no | defaults in the README | Leave them unless there is a reason. **Tax rates: owner to review** (`Tax__StateRates`) |

## 4. The website: settings

The website refuses to start in production when a required value is missing or weak (it never prints the values). Production is anything but `NODE_ENV=development` or `test`, an unset `NODE_ENV` included.

| Setting | Secret | Rules | What it is |
|---|---|---|---|
| `NODE_ENV` | no | `production` | |
| `API_BASE_URL` | no | http(s) URL, required | Where the website reaches the API (internal is fine) |
| `PUBLIC_ORIGIN` | no | `https://host[:port]`, required | The website's own public address: canonical URLs, the sitemap, the form-origin checks |
| `SESSION_COOKIE_SECRET` | **yes** | 32+ characters, at least 12 different characters, no placeholder words; **differs from `WEB_FORWARDING_KEY`** | Seals the session cookie and the CSRF tokens. Rotating it signs everyone out |
| `WEB_FORWARDING_KEY` | **yes** | the same rules; **must EQUAL the API's `Web__ForwardingKey`** | Signs the buyer's IP for the API |
| `TRUSTED_PROXIES` | no | comma-separated IPs or CIDRs | Section 2 |
| `APP_ENV` | no | `production` (default for a production build) or `staging` for the test site | Names the deployment |
| `DEMO_ENABLED` | no | `true` only when `APP_ENV` is development, test, testing or staging | Test mode buttons |
| `REAUTH_WINDOW_SECONDS` | no | 1 to 3600, default 600 | How recent a sign-in must be for sensitive changes |
| `PORT` | no | default 3000 | |
| `STRIPE_JS_URL` | no | must stay `https://js.stripe.com/…` in production | Where Stripe.js is loaded from |

The website holds no Stripe key of its own: it shows the API's publishable key.

## 5. PostgreSQL

- **Version: owner to decide** (CI tests on PostgreSQL 16).
- Create the database with **UTF-8 encoding** (`ENCODING 'UTF8'`); the app stores names, addresses and Yiddish and Hebrew text, which other encodings cannot hold. A UTF-8 locale (for example `en_US.UTF-8` or `C.UTF-8`) is fine; sorting that must be stable uses binary comparison in code, not the database locale.
- The extension **`pg_trgm`** must be available: the first migration runs `CREATE EXTENSION IF NOT EXISTS pg_trgm` (search). Either give the app's role the right to create it, or have an administrator run that once before the first start (Azure Database for PostgreSQL: allow-list `PG_TRGM` in the server's `azure.extensions` parameter first).
- The app's role needs to create tables, indexes and a schema (the start compares an existing database with the migrations in a temporary schema).
- Migrations run at start (`Database__MigrateOnStart=true`, safe with several instances: a database lock serialises them). Backups and retention: **owner to decide**.

## 6. What must not be public

- `/api/admin/*` answers only on `Admin__Host` and only with `Admin__ApiKey`; never route it through the public host or the website.
- `/api/v1/staff/*` is the staff API: limit it with `Staff__AllowedIps__*` and, if possible, at the network.
- `/health` is fine to expose to the platform's health check.

## 7. Stripe (test mode only)

1. In the Stripe dashboard (test mode), create a webhook endpoint with the URL **`https://<API public address>/api/stripe/webhooks`** (section 1).
2. Select these events, which are the ones the API handles: `payment_intent.succeeded`, `payment_intent.processing`, `payment_intent.payment_failed`, `charge.refunded`, `charge.refund.updated`, `refund.updated`, `charge.dispute.created`, `charge.dispute.updated`, `charge.dispute.closed`, `charge.dispute.funds_withdrawn`, `charge.dispute.funds_reinstated`. Other events are acknowledged and ignored.
3. Copy the endpoint's signing secret (`whsec_…`) into `Stripe__WebhookSecret`, the test secret key into `Stripe__SecretKey` and the test publishable key into `Stripe__PublishableKey`.
4. Live keys are refused by both apps. Going live is a separate decision (**owner to decide**, out of scope here).

**The CI job with real Stripe test keys** (workflow `stripe-journey.yml`) is separate from the deployment: it needs the repository secrets `STRIPE_TEST_SECRET_KEY` (`sk_test_…`, recommended for the first run) and `STRIPE_TEST_PUBLISHABLE_KEY` (`pk_test_…`, for the 3DS step), set in GitHub, not in the apps. **It runs only by hand and weekly, never on push or pull request, until one manual run has passed with real test keys** (decision W3-05): several of the Stripe facts it relies on are UNVERIFIED, and a wrong one must not block merges. Its failures name the assumption that broke (A1 to A4). Details: the README, "The real-Stripe journey".

## 8. Ordering and paying by phone (Voice, Sola test key)

Off by default. Turn it on only with every setting below. The full design, limits and the PCI checklist are in docs/VOICE.md (sections 4, 6 and 7). Settings come from the worker who owns voice and payments (decisions V-49 and V-50). **Test keys only:** a key whose value names itself live (the word "live" between separators) stops start-up, in the API and in the connector alike. Every secret must be generated separately and differ from every other secret. The only exceptions are the **EQUAL pairs** in the second table.

### On the API

| Setting | Secret | Rules |
|---|---|---|
| `Voice__Enabled` | no | `true` |
| `Voice__PhonePaymentProvider` | no | `sola` (`none` = no payment by phone; `fake` is refused in production) |
| `Voice__PublicBaseUrl` | no | the API's public https origin (SignalWire calls it; WebSockets must reach it, section 1) |
| `Voice__OpenAiApiKey` | **yes** | |
| `Voice__StreamTokenKey`, `Voice__PhoneCodeKey`, `Voice__CallerHashKey` | **yes** | 32+ characters each, all three different, and different from `Auth__PhoneCodeKey` |
| `SignalWire__ProjectId`, `SignalWire__Space`, `SignalWire__FromNumber` | no | from the SignalWire project |
| `SignalWire__ApiToken`, `SignalWire__SigningKey` | **yes** | from the SignalWire project |
| `Sola__ApiKey` | **yes** | the Sola **TEST** key; required in production (refunds go through it). Start-up logs `Sola: mode unknown` |
| `Sola__PaymentSiteUrl` | no | https (the PaymentSITE page for text-link payments) |
| `Sola__WebhookPin` | **yes** | 15+ letters or digits |
| `Sola__PayConnectorUrl` | no | the connector's public https URL, **no credentials in it** |
| `Sola__PayConnectorUser` | no | |
| `Sola__PayConnectorPassword` | **yes** | 24+ characters |
| `Sola__PayConnectorKey` | **yes** | 32+ characters |
| `Sola__SavedCardKey` | **yes** | 32+ characters. It encrypts saved-card tokens: **losing it makes every saved card unreadable**, so back it up like a database key |
| `Sola__TimeoutSeconds` | no | optional, 5 to 60, default 20 |
| `Voice__Google__MapsApiKey` | **yes** | optional: turns on address help (suggestions for the delivery address). A key for a Google Cloud project with billing and **Places API (New)** enabled; restrict it to this server's IP addresses and to Places API (New) only (Google's recommendation); never in a web client. Without it address help is off with one warning. See the paragraph below |
| `Voice__Google__AllowSaveConfirmedAddress` | no | `false` (default) until the owner/legal confirm Google's terms allow saving the caller-confirmed address; see below |
| `Voice__Google__SpokenAttribution` | no | `true` (default): the first suggestion is introduced with "According to Google Maps" |

**Address help (Google Places).** Off until `Voice__Google__MapsApiKey` is set. It uses the Places API (New) only (the legacy API cannot be enabled for new projects). Two owner decisions are open and are NOT made in code: (1) Google's terms say Places content must not be stored beyond the allowed exceptions and the pages read do not say whether the caller-confirmed street address may be saved into an order or account (the Service Specific Terms decide: ask counsel); until you say so `AllowSaveConfirmedAddress` stays `false`, and the suggestion only helps the caller (the assistant reads it, the caller says yes, and the address is entered the usual way); (2) whether spoken use needs the attribution. Billing: each address attempt in a call is one session of autocomplete requests ending in one Place Details request for Essentials fields only. Customers with an EEA billing address have different terms: check them. The facts and the open points are in docs/agent-briefs/google-places-facts.md.

### On the pay connector host (`src/Solvelt.PayConnector`, a separate service with its own secret store)

| Setting | Secret | Rules |
|---|---|---|
| `Connector__ApiBaseUrl` | no | the API's https origin |
| `Connector__PublicUrl` | no | exactly the URL SignalWire calls, without credentials |
| `Connector__BasicUser` | no | **must EQUAL the API's `Sola__PayConnectorUser`** |
| `Connector__BasicPassword` | **yes** | **must EQUAL the API's `Sola__PayConnectorPassword`** |
| `Connector__SharedKey` | **yes** | **must EQUAL the API's `Sola__PayConnectorKey`** |
| `Connector__SignalWireSigningKey` | **yes** | **must EQUAL the API's `SignalWire__SigningKey`** |
| `Connector__SolaKey` | **yes** | the Sola TEST key (xKey) |
| `Connector__TrustForwardedProto` | no | `true` **only** behind a platform that terminates TLS and sets `X-Forwarded-Proto` (for example Azure App Service) |
| `Connector__LedgerPath` | no | a file on a **persistent** disk. Protect it like a secret: it may briefly hold a Sola token |
| `Connector__MaxChargeCents` | no | optional, default 50000 |
| `Connector__SolaTimeoutSeconds` | no | optional, 5 to 60, default 25 |

These four pairs are the only values two services share. Changing one side means changing the other side at the same time.

### Outside the apps

1. In SignalWire, point `<Pay>` at the connector's URL with its Basic credentials (`https://<user>:<password>@<connector host>/connector`, the same user and password as above).
2. In the Sola account, point the webhook at **`https://<API public address>/api/v1/voice/sola/webhook`**, with the same PIN as `Sola__WebhookPin`.
3. The seller portal's `SolaPaymentProvider` migration is applied on the portal side (**owner or the seller-portal team**; this repository does not change the portal).
4. After start-up the API's log says `Sola: mode unknown`. "Not configured" or "LIVE" means a setting is wrong: do not take calls.
5. Host requirements (one instance or session affinity, WebSockets on): docs/VOICE.md section 7. The connector host is its own cardholder-data environment (docs/VOICE.md section 6, PCI checklist).

## 9. The seller portal

- The portal sends its events to **`https://<API public address>/api/portal/webhooks`**, signed with the secret the API holds as `Portal__WebhookSecret`. Our calls to the portal are signed with `Portal__InboundSecret`. Both values must be set on the portal side too: **owner (or the seller-portal team) to set**; the portal's own setting names are in the portal repository's docs.
- The API reads the portal's catalog feed from `Portal__BaseUrl` (`Catalog__Source` defaults to `Http` outside development).

## 10. The first staff admin

There is no built-in staff account or password. Once, after the first deployment:

1. Generate a long secret. On the API set `Staff__BootstrapToken` to it and `Staff__BootstrapEmail` to the admin's email; restart.
2. Open `https://<website>/staff/enrol`, paste the secret, create a passkey on the device. You are signed in as admin.
3. Remove `Staff__BootstrapToken` and `Staff__BootstrapEmail`; restart. The secret already stopped working when the passkey was made.
4. Add a second passkey on another device, then invite other staff. Details: docs/STAFF.md.

## 11. A short checklist

- [ ] Every secret generated separately; `Web__ForwardingKey` equals `WEB_FORWARDING_KEY`; with phone payments also the four connector pairs of section 8; nothing else is shared.
- [ ] `PUBLIC_ORIGIN`, `Marketplace__PublicBaseUrl`, `Auth__WebAuthnOrigins__0` are the website's https address; `Auth__WebAuthnRpId` its domain (decided once).
- [ ] `Notifications__PublicApiBaseUrl` is the API's public https address, and the public paths of section 1 reach the API.
- [ ] HTTPS Only on both apps; the API restricted to the website except the public paths.
- [ ] PostgreSQL in UTF-8 with `pg_trgm` available.
- [ ] SMTP set; a test site's mail goes to a sandbox.
- [ ] Stripe test keys and the webhook (URL and the events above).
- [ ] The portal holds the same two portal secrets and posts to `/api/portal/webhooks`.
- [ ] `Auth__PhoneCodeKey` set if accounts made by phone should sign in on the website (needs SignalWire messaging).
- [ ] If phone ordering is on: every section 8 setting with TEST keys, the four EQUAL pairs, SignalWire `<Pay>` and the Sola webhook pointed correctly, the portal's Sola migration applied, and `Sola: mode unknown` in the log.
- [ ] The first staff admin made, then the bootstrap settings removed.
