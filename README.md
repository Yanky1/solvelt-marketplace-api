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
CI (`.github/workflows/ci.yml`) restores, builds with warnings as errors, runs the tests on SQLite, and runs them again against a PostgreSQL service container.
It runs on pushes to `main`, `feat/**`, `fix/**` and on pull requests.

## Configuration (environment variables, `__` for nesting)
| Key | Required in Production | Notes |
|---|---|---|
| `Portal__BaseUrl` | yes | Seller portal base URL |
| `Portal__InboundSecret` | yes, >= 32 chars | Signs our calls to the portal |
| `Portal__WebhookSecret` | yes, >= 32 chars, must differ from the above | Verifies portal webhooks |
| `Web__ForwardingKey` | yes, >= 32 chars, must differ from both portal secrets | Verifies the end-user IP the web server forwards (see docs/API.md) |
| `Auth__EmailLinkKey` | yes, >= 32 chars, must differ from every other key | Signs the verify and reset links in account mail (decision M1-56) |
| `Auth__GuestLinkKey` | yes, >= 32 chars, must differ from every other key | Signs guest order-access links (decision M1-70); `Auth__GuestLinkDays` (30) is their lifetime |
| `Notifications__UnsubscribeKey` | yes, >= 32 chars, must differ from every other key | Signs the unsubscribe links in notification mail (decision M5-08) |
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
| `ConnectionStrings__Default` | yes | |
| `Portal__AttemptTimeoutSeconds`, `Portal__TotalDeadlineSeconds`, `Portal__MaxAttempts`, `Portal__RetryBaseDelayMs` | no | Portal client deadlines: defaults 30, 30, 5, 500 (see decisions M1-09, M1-19) |
| `RateLimits__AuthPerMinute`, `RateLimits__PublicPerMinute` | no | Defaults 10 and 240 per client IP |

Logging never contains URLs, query strings or paths (filters are enforced in code, see decision M1-29).
Outside Development/Testing (any other environment name, Staging included) the app refuses to start when a required value is missing, a key is short, any two keys are equal, or a dev default is used in any slot.
No secrets are stored in the repository.
