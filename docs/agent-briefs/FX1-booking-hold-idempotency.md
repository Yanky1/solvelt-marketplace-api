# FX1. Fix: parallel booking hold with the same idempotency key sometimes returns 500

Approved by Ronald, the repository owner, directly in the buyer orchestrator's chat on 2026-11-17 ("A, fix it now"). This is the ONE exception to the marketplace hold, because the bug can turn CI red and block the voice ordering (V) merges. Fix only this bug; touch nothing else.

## The failure
- CI run 42269731980 on main 3ed4e08 (2026-11-06), job "Build and test (SQLite)".
- `BookingHoldTests.The_same_idempotency_key_is_one_booking_and_one_portal_booking_even_when_sent_in_parallel` (tests/Solvelt.Marketplace.Api.Tests/BookingHoldTests.cs:74): `Assert.All()` failed because 1 of 12 parallel requests returned 500 Internal Server Error.
- 2067 other tests passed, and the PostgreSQL job passed. The failure is intermittent.

## What to do
1. Reproduce it: run that test in a loop, and under load (several runs at once), on SQLite and on PostgreSQL if one is available. Capture the exception behind the 500 (log it in the test output, or check the problem details/correlation id).
2. Find the ROOT CAUSE in the booking-hold path. Likely candidates:
   - a unique-constraint race on the idempotency key that isn't turned into "return the existing booking";
   - SQLite busy/locked errors not retried;
   - a race in the portal-booking call.

   Real users sending a double-click would see the same 500, so this is a product bug, not just a test problem.
3. Fix it so that every one of the parallel requests gets the same successful result (one booking, one portal booking), on both SQLite and PostgreSQL. Reuse the idempotency pattern already used elsewhere in the code (check how checkout or cart handles the same race; see docs/DECISIONS.md). Don't invent a new one.
4. Tests:
   - The existing test stays exactly as it is; never skip, weaken, retry-wrap or quarantine it.
   - Add a test that fails without your fix: for example, more parallel requests, or a deterministic interleaving using a test hook. Add a PostgreSQL variant if the race also applies there (`[PostgresFact]`).
5. Record the cause and the fix in docs/DECISIONS.md (FX1-01), and add an "FX1" row to docs/AGENTS.md with your session id.

## Merging
- Branch `fix/fx1-booking-hold-idempotency`, rebased on current origin/main.
- Tell the orchestrator (session_018hZb8pE5JAKd7eu3Enzfnr) the sha before you push.
- Merge only when every workflow is green on that exact commit. Fast-forward push, or a PR with rebase-merge if the push is refused. If the PR route is refused too, stop and tell the orchestrator.
- Worker V (voice) also merges to this repo. If V's merge lands first, rebase onto it and wait for green again.
- CI logs may be blocked from your sandbox; ask the orchestrator for the [FAIL] lines.
- When merged, report to the orchestrator: the sha, the cause, the tests added. Then stop.
