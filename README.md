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
Tests that need PostgreSQL (the schema check against a real server, the migrations) return at once when `TEST_DB_PROVIDER=postgres` and `TEST_POSTGRES_CONNECTION` are not set, so a plain `dotnet test` passes them WITHOUT having run them; only CI's PostgreSQL job (or a local run with those variables) runs them. Nothing in CI is skipped or filtered out.

CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, runs the tests on SQLite, and runs them again against a PostgreSQL service container.
It runs on pushes to `main`, `feat/**`, `fix/**` and on pull requests.

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
| Key | Required in Production | Notes |
|---|---|---|
| `Portal__BaseUrl` | yes | Seller portal base URL |
| `Portal__PublicBaseUrl` | no (defaults to `Portal__BaseUrl`) | The portal's public API origin: the only origin a return-label link may have (decision M5-10) |
| `Portal__InboundSecret` | yes, >= 32 chars | Signs our calls to the portal |
| `Portal__WebhookSecret` | yes, >= 32 chars, must differ from the above | Verifies portal webhooks |
| `Web__ForwardingKey` | yes, >= 32 chars, must differ from both portal secrets | Verifies the end-user IP the web server forwards (see docs/API.md) |
| `Auth__EmailLinkKey` | yes, >= 32 chars, must differ from every other key | Signs the verify and reset links in account mail (decision M1-56) |
| `Auth__GuestLinkKey` | yes, >= 32 chars, must differ from every other key | Signs guest order-access links (decision M1-70); `Auth__GuestLinkDays` (30) is their lifetime |
| `Auth__WebAuthnRpId`, `Auth__WebAuthnOrigins__0`... | yes in production | Passkeys (decision M1-80): the registrable domain the web site is served from (for example `solvelt.com`, no scheme or port) and the exact https origins of the site (each on that domain). A passkey is bound to the RP id for ever: changing it orphans every registered passkey. `Auth__WebAuthnRpName` (Solvelt) is shown by the browser; `Auth__PasskeyChallengeMinutes` (5), `Auth__MaxPasskeys` (10). Development and tests default to `localhost` / `http://localhost:3000` |
| `Checkout__QuoteKey` | yes, >= 32 chars, must differ from every other key | Keys the HMAC that binds a quote (price, tax, delivery, destination) to a checkout and fixes its 15-minute expiry (decision M3-30); rotating it only makes open quotes ask the buyer to confirm again |
| `Notifications__UnsubscribeKey` | yes, >= 32 chars, must differ from every other key | Signs the unsubscribe links in notification mail (decision M5-08) |
| `Notifications__PublicApiBaseUrl` | yes in production, an https URL (dev: defaults to `Marketplace__PublicBaseUrl`) | The origin of the one-click `List-Unsubscribe` URL in notification mail: the public origin that reaches this API (decision M5-17) |
| `Admin__ApiKey` | no (unset or blank disables `/api/admin/*`); when set >= 32 chars, must differ from every other key | Key for the operations endpoints (outbox list/retry) |
| `Admin__Host` | yes when `Admin__ApiKey` is set; must not be the public host | The host (and port) the operations endpoints answer on; everything else gets 404 (decision O-12). Never proxy `/api/admin` from the web server |
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

### Test site (demo data)

`Demo__Enabled=true` turns on test mode (M9): the public config says so (the web shows a TEST MODE badge), buyers get a "Fill with demo data" button, and an unverified account can be verified without email. **The API refuses to start with it on unless `ASPNETCORE_ENVIRONMENT` is exactly Development, Testing or Staging (trimmed, case-insensitive; an allowlist, so "Prod", " Production" and "Live" are refused too), with no override.** A test site therefore runs with `ASPNETCORE_ENVIRONMENT=Staging` (every production check still applies in Staging) and the web with `APP_ENV=staging` and `DEMO_ENABLED=true`. A production build with `Demo__Enabled` off or unset starts normally. Details: docs/API.md "Demo data", docs/DECISIONS.md M9-01 to M9-07.
No secrets are stored in the repository.

## Tools
- `tools/unicode/generate_normalization.py` regenerates the Unicode tables of the managed normaliser (decision M1-81); `dotnet run -c Release --project tools/NormalizationCheck` compares the normaliser with the runtime's ICU-backed one (needs ICU installed).
