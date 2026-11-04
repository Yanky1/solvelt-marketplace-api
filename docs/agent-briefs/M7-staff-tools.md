# Brief M7: marketplace staff tools (API, minimal web)
**Decision (O-06):** the portal admin stays the place for sellers, listings and moderation. The marketplace needs a small staff area of its own only for what the portal can't see: buyer accounts (lookup with masked PII, break-glass reveal audited, lock/unlock, sign-out everywhere), our orders and payments (Stripe intent/refund/dispute status, failed refunds retry with reason, stuck ingests), our outbox and inbound webhook dead letters (view, retry), and Stripe webhook failures.
**Depends on:** M3 (payments), M5.
**Auth:** separate staff accounts (not buyer accounts), passkeys required, roles Support / Finance / Admin with a permission matrix like the portal's `docs/ADMIN.md`, every action audited with a reason, PII masked by default. Rate limited, IP allow-list option.
**Web:** `/staff` area in the web repo behind staff sessions, noindex, same design system.
