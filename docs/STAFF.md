# Marketplace staff area (M7)

The portal admin stays the place for sellers, listings and moderation (decision O-06). This area is only for what the portal cannot see: buyer accounts, our orders and payments, our outbox and inbound dead letters, and Stripe webhook failures. Decisions: `docs/DECISIONS.md` M7-nn.

## Accounts and sign-in
- Staff are a **separate account type** (`staff_accounts`), never a buyer account. Their session tokens (`stf_…`) live in `staff_sessions`; a buyer's token is a different table and shape. A staff token on a buyer endpoint, or a buyer token on a staff endpoint, is `401`.
- **Passkeys only.** There is no password anywhere. A passkey needs user verification. Up to `Staff:MaxPasskeys` (5) per member.
- Sessions: idle `Staff:SessionIdleMinutes` (30), absolute `Staff:SessionAbsoluteHours` (8). Sensitive actions need a passkey check within `Staff:StepUpMinutes` (5): `403 reauthentication_required` otherwise; `POST /api/v1/staff/auth/reauthenticate/options` then `/reauthenticate`.
- The web area sends the token as `Authorization: Bearer`; the web server keeps it in a `Secure; HttpOnly; SameSite=Strict` cookie of its own (not the buyer cookie) and checks CSRF on every action.
- Everything under `/api/v1/staff` is `Cache-Control: private, no-store` and `X-Robots-Tag: noindex, nofollow`, and is rate limited per client IP (`RateLimits:StaffPerMinute`, 60; sign-in and enrolment use the strict auth bucket).
- **IP allow-list (optional; a blank entry or a single value stops the start, it never means "everyone"; always the list form):** `Staff__AllowedIps__0=203.0.113.0/24`, `Staff__AllowedIps__1=198.51.100.7`. When any entry is set every other address gets `404` on `/api/v1/staff`. The address is the one the web server forwards and signs (`Web__ForwardingKey`), else the peer.

## First staff account (for Ronald)
There is **no default account and no default password**. To create the first admin:
1. Pick a secret of at least 32 random characters (`openssl rand -base64 48`). Set `Staff__BootstrapToken=<secret>` and `Staff__BootstrapEmail=<your address>` (optional `Staff__BootstrapName`) on the API and start it. It must differ from every other key.
2. While the database has **no staff account**, the start creates the admin for that address (not enrolled, no password) and opens an enrolment for `Staff__EnrolmentHours` (24 h).
3. Open `/staff/enrol` on the website, enter the secret, and register a passkey. You are signed in. The secret is then dead: it works once, and a restart does not reopen it after the first passkey exists (a restart before that opens a new window).
4. **Remove `Staff__BootstrapToken` and `Staff__BootstrapEmail`.** The start logs a reminder while they are still set.
5. Add a second passkey (a different device) in your profile, and create the other staff from `Staff accounts`.

A member who loses every passkey gets a new one-time link from an admin (`POST /api/v1/staff/accounts/{id}/enrolment`, with `revokePasskeys` for a lost device). If the only admin loses their passkeys: with the API stopped, run `DELETE FROM staff_sessions; DELETE FROM staff_enrolments; DELETE FROM staff_passkeys; DELETE FROM staff_accounts;` (NOT `staff_audit`, which is the record of what was done) and bootstrap again with a NEW secret. The first bootstrap secret works once ever and cannot be reused, even if it is still configured.

A new enrolment link for a member who already has a working passkey must also remove their passkeys and end their sessions (tick "lost device"): otherwise one admin could quietly add their own key to another admin's account.

## Permission matrix
Enforced in code (`StaffPermissions.Matrix`); a test keeps this table equal to it.

| Permission | Support | Finance | Admin | What it allows |
|---|---|---|---|---|
| `buyers.view` | yes | yes | yes | Look up buyer accounts (masked email, phone and address) |
| `buyers.reveal` | yes | - | yes | Break-glass reveal of one buyer's real email, phone and addresses (passkey check, reason, audited) |
| `buyers.lock` | yes | - | yes | Lock and unlock a buyer account |
| `buyers.sign_out` | yes | - | yes | End every session of a buyer |
| `orders.view` | yes | yes | yes | Our orders (masked), their state and money |
| `payments.view` | - | yes | yes | Stripe intent, refund and dispute status of an order |
| `refunds.retry` | - | yes | yes | Retry a failed refund with a reason (through the existing idempotent handler) |
| `ingests.view` | yes | yes | yes | Orders that did not reach the portal |
| `ingests.retry` | yes | - | yes | Send a stuck order to the portal again |
| `outbox.view` | yes | yes | yes | Outbox messages (never the payload) |
| `outbox.retry` | - | - | yes | Re-send a dead outbox message |
| `deadletters.view` | yes | yes | yes | Inbound portal events that failed or are parked |
| `deadletters.retry` | - | - | yes | Replay an inbound dead letter |
| `stripe.view` | - | yes | yes | Stripe webhook events that failed |
| `staff.manage` | - | - | yes | Create, disable and re-enrol staff, change roles |
| `audit.view` | - | - | yes | Read the audit log |

## Audit
Every staff action writes one row in the same transaction as the action: **who** (staff id and role), **what** (action, target type and id), **why** (a reason of 5 to 300 characters, required for every action that changes something or shows personal data), **when**. Sign-ins and passkey checks are recorded too. A row holds ids only: never a buyer's name, address or email. A reason that looks like an email address (also spelled "(at)"), a phone number or a card number is refused (`422`), so write the ticket or order number. This filter is best effort: it cannot recognise a name, so never write one. Rows are append only; the only delete is the retention job (`Staff:AuditRetentionDays`, 730).
