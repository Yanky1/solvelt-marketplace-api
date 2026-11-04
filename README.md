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
| `Marketplace__RelayDomain` | yes | Domain of the buyer relay emails |
| `Marketplace__PublicBaseUrl` | yes | Public website URL (links in emails) |
| `Database__Provider` | yes, `Postgres` | `Sqlite` is for dev/tests only |
| `ConnectionStrings__Default` | yes | |
| `Portal__AttemptTimeoutSeconds`, `Portal__TotalDeadlineSeconds`, `Portal__MaxAttempts`, `Portal__RetryBaseDelayMs` | no | Portal client deadlines: defaults 30, 30, 5, 500 (see decisions M1-09, M1-19) |
| `RateLimits__AuthPerMinute`, `RateLimits__PublicPerMinute` | no | Defaults 10 and 240 per client IP |

Outside Development/Testing (any other environment name, Staging included) the app refuses to start when a required value is missing, a key is short, any two keys are equal, or a dev default is used in any slot.
No secrets are stored in the repository.
