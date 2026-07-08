# Feature 1 — Early-Warning Watchlist

## What it is

A ranked list of the cost centres that are currently **GREEN** but are most likely to tip to
**AMBER** in the next reporting period. It gives the QS a short, prioritised list to act on
while money is still unspent — the core "see trouble early" promise of the product.

## Who it's for

The project QS who wants a daily/monthly triage: *"Of everything that still looks fine, where
should I look first?"*

## How it works

- The honest event being predicted is **`AlertLevel(p+1) == "AMBER"`**, which on this data
  coincides exactly with next-period **`CPI < 0.95`**.
- Only centres that are **GREEN at period `p`** are scored (you can't warn about something
  already AMBER).
- Each centre gets a transparent, frozen score, **`RuleRiskScore@v1`**:
  `0.7·clamp01((gap − x*) / gap_scale) + 0.3·cpiProximity`, where
  `cpiProximity = clamp01(1 − (CPI − 0.95) / 0.10)` peaks at the 0.95 line.
  Only `x*` and `gap_scale` are fitted, and only on training data — nothing is tuned on the
  rows it's evaluated against.
- Centres are ranked by score; the top `k` (5 or 10) are the watchlist.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/watchlist?period={p}&k={5\|10}` | Ranked GREEN-about-to-tip centres for the caller's selected project. |

For Tower X the valid `period` range is currently **4–12**; in general the servable origins are
derived dynamically from the project's own periods.

Errors: `401` no identity · `403` not a member of the project · `404` unknown project / valid
period with no matching model artifact · `400` malformed `period`/`k`.

## UI

The **Watchlist** tab. A ranked table (rank, cost centre, score, current CPI, and a
**budget/progress gap** — `Pct_Budget_Consumed − Actual_Pct_Complete`) for the selected period
and `k`.

## Guarantees & limits

- **Transparent, not a black box** — the score is a published formula, not an opaque model.
- **No leakage** — each period is scored by a model trained strictly on its past
  (see [Model Validation Panel](06-model-validation-panel.md) for the measured accuracy).
- **Single-project evidence** — validated on the Tower X workbook only; no cross-project
  generalisation is claimed.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _"gap-to-tip" mislabels the UI column_ — it's `Pct_Budget_Consumed − Actual_Pct_Complete`.
  **Fixed:** renamed to "budget/progress gap" with the formula.
- _Missing project data returns `403`, not `404`_ — **Fixed:** the error list distinguishes
  `403` (not a member) from `404` (unknown project / no-artifact).

**Round 2 (CHANGES REQUESTED) — resolved.**

- _Error list contradicted itself — this controller returns `404` (not `403`) for an unknown
  project_ — **Fixed:** `403` is now "not a member of the project" only; unknown project is `404`.
- _`{4..12}` is Tower X-specific_ — **Fixed:** labelled as Tower X's current range; origins are
  otherwise derived dynamically.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
