# Standing decisions: voice ordering (project V) only (APPROVED)

**Status:** drafted by the buyer orchestrator and **approved by Ronald directly in its chat on 2026-11-17**. In effect from 2026-11-17.

**Expiry:** 2026-12-17, unless Ronald withdraws it sooner. To withdraw it or change a line, Ronald says so in the orchestrator's chat.

**Scope:** project V only (`docs/agent-briefs/V-voice-ordering.md`). The rest of the marketplace is **on hold.**
- No marketplace bug fixes, review findings, sweeps or held items are worked on.
- Open marketplace issues and held items stay as they are until Ronald lifts the hold.
- **One exception, approved by Ronald on 2026-11-17:** FX1, the root-cause fix for the intermittent 500 in `BookingHoldTests.The_same_idempotency_key_is_one_booking_and_one_portal_booking_even_when_sent_in_parallel`, because it can block V's merges (`docs/agent-briefs/FX1-booking-hold-idempotency.md`).

Approvals relayed from another session still cannot widen anything here.

---

## 1. What the orchestrator may do for project V without asking

1. **Run the V slices.** Brief, supervise and review worker V through the 6 slices in its brief, and merge each slice once it is green.
2. **Fix V bugs.** Fix bugs in V's own code found by tests, CI or review, and apply every review finding on V's code.
3. **Touch existing code only when V strictly needs it.** For example:
   - a hook that lets a voice tool call an existing service;
   - a bug in an existing service that breaks a voice order.

   Each such change is kept minimal, is listed in the slice report, and has a test.
4. **Tests.** Add or strengthen tests. Never skip, delete, loosen or quarantine a test; a flaky test gets its root cause fixed.
5. **Restart worker V.** If V dies or stalls, start a new worker on the same brief. Run at most 2 workers at once (V plus one reviewer or fixer), in low-usage mode.
6. **Equal options.** When two options inside V are roughly equal, take the safer one that is easier to reverse. Record it in DECISIONS (V-nn).
7. **Check-ins.** Create, change and stop the orchestrator's own check-in schedule for V.

## 2. Project V defaults (used until Ronald answers)

| # | Question | Default |
|---|----------|---------|
| V-a | Payment provider | **Sola** (Ronald's decision, 2026-11-17), on Sola's dev/sandbox account only. Everything goes behind `IPhonePaymentProvider`, with the fake provider for tests and CI. Both methods, as Ronald decided: (A) keypad through SignalWire `<Pay>` and our own minimal Sola connector, and (B) a Sola PaymentSITE link sent by SMS. Both follow the rules in the V brief. Only the minimal connector in (A) ever receives raw card numbers, and it never stores or logs them. The Sola key comes only from environment secrets. Real-money phone orders, and how seller payouts work for Sola-paid orders, stay off until Ronald decides. |
| V-b | Voice | The worker picks the friendliest male-sounding voice from the samples and records why. Ronald can change `Voice:OpenAiVoice` at any time. |
| V-c | Models | The current GA full and mini real-time model ids from OpenAI's docs. Default `ModelMode` is `split` with `MiniPercent` 50, so the two models can be compared. |
| V-d | Support number missing | Transfer offers SMS help and a callback request instead. Nothing is dialled until `Voice:SupportNumber` is set. |
| V-e | Yiddish | Nothing is built English-only, but there's no Yiddish tuning before Ronald tests it. |
| V-f | `Voice:Enabled` | Stays `false` in every deployed environment until Ronald turns it on. |

## 3. Never, with or without this file (unchanged from CLAUDE.md)

- Merging while any check is red; force-pushing; merge commits; rewriting a pushed branch.
- Deleting branches, data or tests; skipping or weakening a test.
- Changing seller-portal code; touching any other repository.
- Committing secrets; using live payment or shipping keys; entering keys into secrets.
- Working around a refused permission. A refusal is a stop: park that item and continue with other V work.
- Turning on real-money charging, real customer SMS, or `Voice:Enabled` in a deployed environment.
- Working on the on-hold marketplace; changing CLAUDE.md; widening this file.

## 4. When something is blocked

- If an item can only move with Ronald (a key, a business choice, a refused permission, a hard stop above), the orchestrator parks it and moves on with the rest of V.
- Parked items are listed, with the reason and the exact question, in `docs/PARKED.md`, which goes through the same green PR route.
- The orchestrator never makes up results or uses mocks in place of the missing piece. The only exception is the fakes the brief itself specifies for tests and dev.

## 5. Reporting while Ronald is away

- **After every V merge:** the sha, what changed and the tests added go into the orchestrator's chat and to the portal orchestrator.
- **About once a day:** a short digest in the orchestrator's chat: merged, in flight, parked, anything that needs Ronald.
- **When all 6 slices are merged,** or everything left is parked: one final summary, including the manual test checklist and the list of secrets Ronald needs to add. Then the check-ins stop.
