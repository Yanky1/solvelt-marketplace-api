# Solvelt marketplace API

The buyer-side backend of the Solvelt marketplace. It handles buyer accounts, catalog and search read models, cart, checkout (Stripe) and bookings. It talks to the seller portal through the portal's signed internal API and outbox webhooks.

- Built by the buyer-marketplace orchestrator. Its brief is [docs/agent-briefs/ORCHESTRATOR.md](docs/agent-briefs/ORCHESTRATOR.md).
- The standing rules for Claude sessions are in [CLAUDE.md](CLAUDE.md).
- The sister repo is [solvelt-marketplace-web](https://github.com/Yanky1/solvelt-marketplace-web), the buyer website (React + React Router v7, server-rendered).
