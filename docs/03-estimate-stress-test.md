# Feature 3 — Estimate Assumption Stress Test

## What it is

A pre-execution review of the **estimate** itself: it surfaces aggressive or unusual
assumptions (optimistic Output Norms, thin unit rates, thin/zero contingency) so a QS can
challenge them *before* the work is awarded — the only feature that attacks cost trouble at
day zero rather than tracking it after the fact.

## Who it's for

The QS/estimator reviewing a tender or estimate package before award.

## How it works

Output is split into three explicitly separated classes so their evidentiary weight is never
confused:

1. **Class 1 — Reconciliation tie-out.** Proves the bottom-up estimate math ties out end to
   end (resource costs → BOQ → contract total), applying the Output-Norm correction
   (manpower/equipment qty = `BOQ qty × count ÷ Output Norm`). This is a correctness proof.
2. **Class 2 — Assumption flags.** Day-zero review prompts (reads zero actuals), each with an
   exact, cohort-gated threshold: aggressive **Output Norm** (top decile within its
   sub-trade+unit cohort), thin **Unit Rate** (bottom decile), and thin/zero **contingency**.
3. **Class 3 — Peer benchmark (RETROSPECTIVE only).** Compares a package against same-project
   peers. Because those peer actuals don't exist at award time, this class is scoped as
   retrospective validation, **not** a day-zero product signal.

When a project has no estimate workbook, each endpoint returns `available: false`.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/stress-test/reconciliation` | Class 1 tie-out. |
| `GET /api/v1/stress-test/assumptions?discipline={value}` | Class 2 assumption flags (`discipline` optional). |
| `GET /api/v1/stress-test/peer-benchmark` | Class 3 retrospective peer benchmark. |

## UI

The **Stress Test** tab (`StressTest`).

## Guarantees & limits

- **Not an under-pricing oracle** — with a single project it flags assumptions *for review*; it
  does not prove that anything is objectively under-priced.
- **Day-zero product = Classes 1 + 2.** Class 3 is retrospective because same-project peer
  actuals aren't available at award.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _"Risky notes" flag isn't implemented_ — Class 2 checks only Output Norm, unit rate, and
  contingency. **Fixed:** removed "risky notes"; listed the three real, cohort-gated checks.
- _`/assumptions` supports optional `discipline`_ — **Fixed:** documented `?discipline={value}`.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
