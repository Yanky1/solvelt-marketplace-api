# Solvelt marketplace API

The buyer-side backend of the Solvelt marketplace: buyer accounts, catalog read model, cart, checkout (Stripe) and bookings.
It talks to the seller portal through the portal's signed internal API and outbox webhooks (see [docs/CONTRACT.md](docs/CONTRACT.md)).

- Orchestrator brief: [docs/agent-briefs/ORCHESTRATOR.md](docs/agent-briefs/ORCHESTRATOR.md). Standing rules for Claude sessions: [CLAUDE.md](CLAUDE.md).
- Sister repo: [solvelt-marketplace-web](https://github.com/Yanky1/solvelt-marketplace-web) (React Router v7, SSR).
- Endpoints: [docs/API.md](docs/API.md). Decisions: [docs/DECISIONS.md](docs/DECISIONS.md).

## Stack
.NET 10, ASP.NET Core minimal APIs, EF Core. SQLite for dev and tests, PostgreSQL (Npgsql) in production.

## Run
```bash
dotnet run --project src/Solvelt.Marketplace.Api        # Development: SQLite file, fixed dev secrets
```
`ASPNETCORE_ENVIRONMENT=Development` is the default for `dotnet run`. `GET /health` answers `{"status":"ok"}`.

## Test
```bash
dotnet test                                               # real SQLite, one temp database per test factory
TEST_DB_PROVIDER=postgres \
TEST_POSTGRES_CONNECTION="Host=localhost;Username=postgres;Password=postgres;Database=postgres" \
  dotnet test                                             # the same tests against PostgreSQL (a fresh database per factory)
```
Tests that need PostgreSQL (the schema check against a real server, the migrations, the trigram and tsvector checks, a few races: 26 tests, 34 with their theory rows) carry the attribute `[PostgresFact]` or `[PostgresTheory]` and the trait `Db=Postgres` (decision M8-15). With `TEST_DB_PROVIDER=postgres` and `TEST_POSTGRES_CONNECTION` set they run; without them a plain `dotnet test` reports them as **skipped** with the reason "needs PostgreSQL: set TEST_DB_PROVIDER=postgres and TEST_POSTGRES_CONNECTION", never as passed (they used to `return` early and pass without having run). `dotnet test --filter "Db!=Postgres"` leaves them out, which is what CI's SQLite job does (0 skipped); CI's PostgreSQL job runs everything.

CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, runs the tests on SQLite, and runs them again against a PostgreSQL service container.
It runs on pushes to `main`, `feat/**`, `fix/**` and on pull requests.

## Buyer journey (real portal)
`tools/journey/run.sh` starts the real seller-portal backend, this API on PostgreSQL, the web server and a mail sandbox, and runs the buyer journeys in a real browser (decisions M8-01 to M8-06, M8-16): product (ending with the buyer's verified review), booking, refund, and the staff area (a passkey enrolled from the one-time bootstrap secret, a masked buyer look-up, lock and unlock, the audit log). Needs .NET 10, Node 22, PostgreSQL (`JOURNEY_PG_*`, default `postgres:postgres@localhost:5432`), and checkouts of the portal (`PORTAL_DIR`) and the web (`WEB_DIR`); `PW_CHROMIUM_PATH` picks a Chromium. `tools/journey/stack.sh up|down|logs <name>` alone leaves the stack running (logs in `.journey/`). CI: `.github/workflows/journey.yml`. A missing portal fails the job, it never falls back to a mock.

### The real-Stripe journey (workflow `stripe-journey.yml`, job `journey-real-stripe`): what the owner adds

**It runs only by hand (Actions, "Real-Stripe journey", Run workflow) and weekly (Mondays), never on push or pull request**, until one manual run has passed with real test keys (W3-05): several facts it relies on about Stripe could not be checked when it was written (marked UNVERIFIED in `tools/journey/stripe-journey.mjs`; a failure names the assumption that broke, "ASSUMPTION A1" to "A4"), and a wrong one must not block every merge. After that first passing run a follow-up may move it into the push workflow.

The job is SKIPPED (a notice, never a failure) until these **repository secrets** exist (GitHub: Settings, Secrets and variables, Actions, New repository secret). Use the keys of a Stripe account in **test mode** only; both apps refuse live keys.

| Secret | Value | What it turns on |
|---|---|---|
| `STRIPE_TEST_SECRET_KEY` | the test secret key, **`sk_test_…` (recommended for the first run)**; a restricted test key `rk_test_…` also works in principle, but the permissions it needs (PaymentIntents and Refunds write, Events read) are UNVERIFIED | The job itself: an order paid through Stripe's API with the test card `pm_card_visa`, Stripe's OWN `payment_intent.succeeded` event read back from Stripe and delivered to the API signed with the webhook secret (twice: the second delivery changes nothing), then a seller refund made at Stripe and read back from Stripe and the portal ledger |
| `STRIPE_TEST_PUBLISHABLE_KEY` | the test publishable key of the SAME account, `pk_test_…` | The 3DS step: the 3DS test card in a real browser through the real Stripe.js on the checkout page, the test challenge completed, the confirmation page reached, Stripe's own event delivered signed, and the order placed with the seller. Without it this step is skipped with a notice |

No webhook endpoint is needed in the Stripe dashboard for this job: Stripe cannot reach a CI runner, so the event is fetched from Stripe's API and replayed with the webhook secret the job's API uses (decisions M8-09, W3-05). The keys are given only to the step that runs the journey; `tools/journey/test-key-scope.sh` checks that no other process of the stack sees the secret key, and nothing writes either key to a file or a log.

## Database schema (existing databases)
Development and Testing build the schema from the model with `EnsureCreated`, which creates a database once and never alters it. A database made by an earlier build therefore lacks the tables and columns added since, and the first request that touches one answers 500. At start the API compares the database with the model and logs ONE critical line naming the differences (it does not stop). To see them and get the SQL that adds what is missing (read-only: SQLite is opened with `Mode=ReadOnly`, so a mistyped path fails instead of creating an empty database, and PostgreSQL with `default_transaction_read_only`; the connection string is taken from `MARKETPLACE_DB` or standard input, never from the command line; review the SQL and run it in a transaction on a copy first):
```bash
MARKETPLACE_DB="Data Source=marketplace-dev.db" dotnet run --project tools/SchemaTool -- sqlite
MARKETPLACE_DB="Host=...;Database=...;Username=...;Password=..." dotnet run --project tools/SchemaTool -- postgres
echo "Host=...;Database=...;Username=...;Password=..." | dotnet run --project tools/SchemaTool -- postgres -     # from standard input
```
Or recreate the development database (delete the file) when its data does not matter.

## Database schema (migrations)
Development and Testing build the schema from the model (`EnsureCreated` + `SearchSchema.Ensure`). Production uses the EF migrations in `src/.../Data/Migrations` (PostgreSQL), made from `tools/MigrationsTool`:
```bash
dotnet tool install --global dotnet-ef --version 10.0.0
./tools/migrations/regenerate-initial-create.sh           # BEFORE the first release only: rebuilds InitialCreate from the current model
dotnet ef migrations add Name --project src/Solvelt.Marketplace.Api --startup-project tools/MigrationsTool --output-dir Data/Migrations   # after the first release
```
A database made earlier by `EnsureCreated` (no migration history) is checked against the model at start and, if it matches, baselined without touching its data; if it differs the start stops with the list of differences and changes nothing.

A database made without migrations is baselined only if it is EXACTLY what the migrations make (they are replayed into a temporary schema, rolled back, and compared object by object: columns, defaults, constraints, indexes including unique and partial ones); otherwise the start stops with the list of differences and changes nothing. The role needs permission to create a schema. `tools/SchemaTool` also reports, read-only and counts only, how many stored emails are not in NFC (made before the normaliser); sign-in and guest lookups normalise to NFC, so such rows no longer match: fix them by hand, for example `UPDATE buyer_accounts SET "Email" = normalize("Email", NFC) WHERE "Email" IS NOT NFC NORMALIZED;` on PostgreSQL 13+ (check for a clash with an existing address first).

**The history names a migration this build does not have** (the database was migrated by another build: an older one whose `InitialCreate` was regenerated before the first release, or a newer one after a rollback): the start stops with the ids and changes nothing. If it was an older build, the one-line fix is `DELETE FROM "__EFMigrationsHistory";` and a restart: the start then compares the schema with the migrations and baselines it, or lists what differs (`tools/SchemaTool` prints the SQL that adds what is missing). If it was a newer build, deploy that build again.

`MigrationTests` (PostgreSQL) fail when the model and the migrations differ (`HasPendingModelChanges`, and a table/column/index/constraint comparison against `EnsureCreated`). The search objects EF cannot model (pg_trgm, the tsvector function and column, GIN indexes) are the frozen `InitialCreate.SearchSchema.cs`; later changes to them are new migrations.

## Configuration (environment variables, `__` for nesting)
Setting up a deployment (both apps, secrets, TLS, the reverse proxy, PostgreSQL, Stripe and portal webhooks, the first staff admin): [docs/DEPLOY.md](docs/DEPLOY.md).

| Key | Required in Production | Notes |
|---|---|---|
| `Portal__BaseUrl` | yes | Seller portal base URL |
| `Portal__PublicBaseUrl` | no (defaults to `Portal__BaseUrl`) | The portal's public API origin: the only origin a return-label link may have (decision M5-10) |
| `Portal__InboundSecret` | yes, >= 32 chars | Signs our calls to the portal |
| `Portal__WebhookSecret` | yes, >= 32 chars, must differ from the above | Verifies portal webhooks |
| `Web__ForwardingKey` | yes, >= 32 chars, must differ from both portal secrets | Verifies the end-user IP the web server forwards (see docs/API.md) |
| `Auth__EmailLinkKey` | yes, >= 32 chars, must differ from every other key | Signs the verify and reset links in account mail (decision M1-56) |
| `Auth__GuestLinkKey` | yes, >= 32 chars, must differ from every other key | Signs guest order-access links (decision M1-70); `Auth__GuestLinkDays` (30) is their lifetime |
| `Auth__PhoneCodeKey` | no; when set >= 32 chars, must differ from every other key, also from `Voice__PhoneCodeKey`, `Voice__StreamTokenKey`, `Voice__CallerHashKey` and the SignalWire secrets | HMAC of the SMS codes the web sends (decision W3-02: sign-in by phone for an account made by phone, and its step-up). Unset turns web sign-in by phone off (503). Needs the SignalWire messaging settings to deliver the texts |
| `Auth__WebAuthnRpId`, `Auth__WebAuthnOrigins__0`... | yes in production | Passkeys (decision M1-80): the registrable domain the web site is served from (for example `solvelt.com`, no scheme or port) and the exact https origins of the site (each on that domain). A passkey is bound to the RP id for ever: changing it orphans every registered passkey. `Auth__WebAuthnRpName` (Solvelt) is shown by the browser; `Auth__PasskeyChallengeMinutes` (5), `Auth__MaxPasskeys` (10). Development and tests default to `localhost` / `http://localhost:3000` |
| `Checkout__QuoteKey` | yes, >= 32 chars, must differ from every other key | Keys the HMAC that binds a quote (price, tax, delivery, destination) to a checkout and fixes its 15-minute expiry (decision M3-30); rotating it only makes open quotes ask the buyer to confirm again |
| `Stripe__SecretKey` | no (unset: the in-process fake provider, development only) | **Test keys only** (`sk_test_`/`rk_test_`); a live key stops the API in every environment; differs from every other key. Secret |
| `Stripe__PublishableKey` | no; expected when `Stripe__SecretKey` is set (a start-up warning otherwise) | `pk_test_` only; public by design (the web shows it to the browser for the Payment Element) |
| `Stripe__WebhookSecret` | yes when `Stripe__SecretKey` is set | The signing secret (`whsec_`) of the dashboard webhook `https://<api>/api/stripe/webhooks`; >= 32 chars, differs from every other key. Secret. Events: docs/DEPLOY.md section 7 |
| `Stripe__BaseUrl`, `Stripe__TimeoutSeconds`, `Stripe__InUseRetryDelayMs`, `Stripe__InUseRetries` | no | Defaults `https://api.stripe.com`, 20, 400, 3 |
| `Stripe__FakeCaptureOnWebhook` | no, Development/Testing only | With the fake provider only: a verified `payment_intent.succeeded` also captures the fake's intent (the journey); refused elsewhere (M8-08) |
| `Notifications__UnsubscribeKey` | yes, >= 32 chars, must differ from every other key | Signs the unsubscribe links in notification mail (decision M5-08) |
| `Notifications__PublicApiBaseUrl` | yes in production, an https URL (dev: defaults to `Marketplace__PublicBaseUrl`) | The origin of the one-click `List-Unsubscribe` URL in notification mail: the public origin that reaches this API (decision M5-17) |
| `Admin__ApiKey` | no (unset or blank disables `/api/admin/*`); when set >= 32 chars, must differ from every other key | Key for the operations endpoints (outbox list/retry) |
| `Admin__Host` | yes when `Admin__ApiKey` is set; must not be the public host | The host (and port) the operations endpoints answer on; everything else gets 404 (decision O-12). Never proxy `/api/admin` from the web server |
| `Staff__BootstrapToken`, `Staff__BootstrapEmail`, `Staff__BootstrapName` | no | One-time creation of the FIRST staff admin (>= 32 chars, different from every other key; the address is required with it). Never a default account or password; remove after the first passkey is registered. Steps: [docs/STAFF.md](docs/STAFF.md) (decision M7-04) |
| `Staff__AllowedIps__0`... | no | Addresses or CIDR ranges allowed to reach `/api/v1/staff`; unset allows all; anyone else gets 404 (M7-06) |
| `Staff__SessionIdleMinutes`, `Staff__SessionAbsoluteHours`, `Staff__StepUpMinutes`, `Staff__EnrolmentHours`, `Staff__MaxPasskeys`, `Staff__AuditRetentionDays`, `Staff__RevealsPerHour`, `Staff__LookupsPerHour`, `Staff__StuckIngestMinutes`, `RateLimits__StaffPerMinute` | no | Defaults 30, 8, 5, 24, 5, 730, 20, 120, 15, 60 |
| `Email__Provider` | yes, `Smtp` (Development/Testing use an in-memory sink) | Without it the application does not start (decision M1-50) |
| `Email__SmtpHost`, `Email__From` | yes with `Smtp` | SMTP server and the sender address |
| `Email__SmtpPort`, `Email__SmtpUserName`, `Email__SmtpPassword`, `Email__SmtpUseTls`, `Email__SendTimeoutSeconds` | no | Defaults 587, none, none, true, 20 |
| `Auth__PasswordIterations`, `Auth__SessionIdleDays`, `Auth__SessionAbsoluteDays` | no | Defaults 210000, 7, 30 (decisions M1-51 to M1-54) |
| `Auth__SignInFreeFailures`, `Auth__SignInMaxDelaySeconds`, `Auth__SignInIpFailuresPerHour`, `Auth__MaxBodyBytes` | no | Sign-in gate and body cap, defaults 5, 900, 20, 16384 (decisions M1-63, M1-67) |
| `Outbox__MaxAttempts`, `Outbox__BackoffSeconds`, `Outbox__MaxBackoffSeconds`, `Outbox__LeaseSeconds` | no | Defaults 8, 30, 3600, 120 (decision M1-47) |
| `Marketplace__RelayDomain` | yes | Domain of the buyer relay emails |
| `Marketplace__PublicBaseUrl` | yes | Public website URL (links in emails) |
| `Database__Provider` | yes, `Postgres` | `Sqlite` is for dev/tests only |
| `Database__MigrateOnStart` | no (default `true`) | Production on PostgreSQL applies the EF migrations at start (a database lock makes several instances safe); `false` to apply them from a pipeline (`dotnet ef database update` with `MARKETPLACE_DB` set, or a migration bundle). Production never uses `EnsureCreated`; the database role must be allowed `CREATE EXTENSION pg_trgm` (or a superuser creates it once) |
| `ConnectionStrings__Default` | yes | |
| `Portal__AttemptTimeoutSeconds`, `Portal__TotalDeadlineSeconds`, `Portal__MaxAttempts`, `Portal__RetryBaseDelayMs` | no | Portal client deadlines: defaults 30, 30, 5, 500 (see decisions M1-09, M1-19) |
| `RateLimits__AuthPerMinute`, `RateLimits__PublicPerMinute` | no | Defaults 10 and 240 per client IP |

Logging never contains URLs, query strings or paths (filters are enforced in code, see decision M1-29).
Outside Development/Testing (any other environment name, Staging included) the app refuses to start when a required value is missing, a key is short, any two keys are equal, or a dev default is used in any slot.

### Staff area: making the first admin (for Ronald)
There is no built-in staff account and no password. In plain words:
1. Make up a long secret (`openssl rand -base64 48`). On the API set `Staff__BootstrapToken` to it and `Staff__BootstrapEmail` to your email, then start the API.
2. Open the website at `/staff/enrol`, paste the secret, and let your phone or laptop create a passkey. You are now signed in as admin.
3. Delete `Staff__BootstrapToken` and `Staff__BootstrapEmail` from the settings. The secret already stopped working the moment you made the passkey.
4. Add a second passkey on another device, then invite the rest of the staff from "Staff accounts". Lost a device? An admin sends a new one-time link. Full details: [docs/STAFF.md](docs/STAFF.md).

### Test site (demo data)

`Demo__Enabled=true` turns on test mode (M9): the public config says so (the web shows a TEST MODE badge), buyers get a "Fill with demo data" button, and an unverified account can be verified without email. **The API refuses to start with it on unless `ASPNETCORE_ENVIRONMENT` is exactly Development, Testing or Staging (trimmed, case-insensitive; an allowlist, so "Prod", " Production" and "Live" are refused too), with no override.** A test site therefore runs with `ASPNETCORE_ENVIRONMENT=Staging` (every production check still applies in Staging) and the web with `APP_ENV=staging` and `DEMO_ENABLED=true`. A production build with `Demo__Enabled` off or unset starts normally. **A test site must send its mail to a sandbox (Mailpit, a catch-all mailbox), never to real addresses:** an unverified account can be demo-verified without mailbox proof, and its notifications go to the address it typed. Details: docs/API.md "Demo data", docs/DECISIONS.md M9-01 to M9-08.
No secrets are stored in the repository.

## Tools
- `tools/unicode/generate_normalization.py` regenerates the Unicode tables of the managed normaliser (decision M1-81); `dotnet run -c Release --project tools/NormalizationCheck` compares the normaliser with the runtime's ICU-backed one (needs ICU installed).

## Voice ordering by phone

Off unless `Voice:Enabled`. Setup (SignalWire number and webhooks, OpenAI key and models, every setting, database step for existing databases, payments guard, known limits, manual listening checklist): [docs/VOICE.md](docs/VOICE.md). Decisions: `V-00` to `V-09` in [docs/DECISIONS.md](docs/DECISIONS.md); endpoints: [docs/API.md](docs/API.md).
