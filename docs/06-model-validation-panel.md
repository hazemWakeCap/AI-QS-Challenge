# Feature 6 — Model Validation Panel

## What it is

An honest, model-level report of how accurate the early-warning scorer actually is, so a QS (or
a reviewer) can trust the watchlist. It shows the frozen out-of-fold back-test — never dressed
up as the selected period's live accuracy.

> **Scope:** this endpoint is **global and unauthenticated** — it reports the process's
> startup **Tower X** workbook model, not the caller's selected project snapshot. Treat it as
> the deployed scorer's reference validation report for Tower X.

## Who it's for

Anyone deciding whether to rely on the watchlist: the QS, a project controls lead, or a
reviewer auditing the claim.

## How it works

- Validation is **rolling-origin** back-testing: 8 folds (origins 4–11), each fold trained
  strictly on its past and evaluated on the next period — no leakage.
- Reported honestly as **per-fold counts + fold range**, not a fragile confidence interval.
- Metrics are computed the honest way: `precision@k = TP / min(k, eligible)`; folds with zero
  positives report recall as N/A rather than inflating the average.
- Headline result — **Tower X-specific**: **precision@5 = 45%** vs **35%** for the best
  CPI-native baseline on this workbook (not a cross-project number).
- The report is **model-level and frozen** — it describes the one deployed rule scorer over
  history, and is never presented as "the accuracy for the period you're looking at."

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/validation-summary` | The frozen out-of-fold validation report for the deployed scorer (global, unauthenticated, Tower X model). |

## UI

The **Model & Copilot** tab (`ValidationPanel`).

## Guarantees & limits

- **Unbiased back-test** — nothing is selected on the eval folds; the deployed rule is
  predeclared, so the numbers aren't cherry-picked.
- **Single-project scope** — the numbers describe this workbook; they are exploratory evidence,
  not a cross-project accuracy claim.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _Endpoint is unauthenticated and backed by the startup Tower X model, not the caller's
  snapshot_ — **Fixed:** added a scope callout and labelled the endpoint global/Tower X.
- _45% vs 35% should stay explicitly Tower X-specific_ — **Fixed:** marked the headline as
  Tower X-specific, not cross-project.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
