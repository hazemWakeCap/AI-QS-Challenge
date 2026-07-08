# Feature 5 — Variance Attribution Bridge

## What it is

The drill-down behind the watchlist. For a chosen cost centre and period it takes the cost
variance (CV) and attributes it to the **dominant resource category**, and separately flags
whether the schedule (SV) is off — answering *"this package varied; which resource drove it,
and is it a cost or a schedule problem?"*

## Who it's for

The QS who's been alerted (by the watchlist or dashboard) and now wants to know *where* the
money went before raising it with the team.

## How it works

- Decomposes CV into a **cost/efficiency lane** broken down by resource category (using the
  estimate resource-mix shares), plus a monetary **schedule lane** reported as `SV = EV − PV`.
- The decomposition **ties out**: `Σ CvR + residual == CV`. The dominant result is the
  top-variance resource category **unless the unexplained residual outweighs it**, in which case
  the dominant contributor is reported as **`"unexplained residual"`** (the four AC splits don't
  fully cover AC).
- When the estimate resource mix **is** available, the attribution carries an
  **assumption-based-attribution badge** (`assumptionBased: true`), because the data can only
  identify the dominant resource *contributor* — it cannot separate price from productivity.
  When the mix is **unavailable**, the response falls back to CV/SV totals only, with
  `assumptionBased: false` and a note.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/variance?bcc={id}&period={p}` | CV decomposition + SV lane + tie-out + attribution badge for one (cost centre, period). |

Attribution is only produced for **live `EP-` estimate packages** — i.e. a package whose code
starts with `EP-` and that has `EV > 0` with finite EV/AC/PV. Anything else returns an
`available: false`/unavailable response.

## UI

Surfaced as a card (`VarianceCard`) when drilling into a cost centre, and via the copilot's
`ExplainVariance` tool.

## Guarantees & limits

- **Attribution, not diagnosis.** It names the dominant resource **contributor** (or
  `"unexplained residual"` when the AC splits don't cover AC); the *cause* (price vs.
  productivity) is a labelled hypothesis, because `9_HISTORICAL_DATA` carries only the four
  `AC_*_AED` category totals and whole-package quantities — no labour hours or per-resource
  rates.
- **Tie-out guaranteed** — the decomposition always reconciles to the reported CV
  (`Σ CvR + residual == CV`).
- **Scope-limited** — only live `EP-` packages with finite EV/AC/PV are attributed; without an
  estimate resource mix the response is CV/SV totals only.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _Schedule lane isn't `Earned_Qty_Period` vs `Planned_Qty_Period`; code computes monetary
  `SV = EV − PV`_ — **Fixed:** replaced the formula.
- _Dominant can be `"unexplained residual"`, not a resource category_ — **Fixed:** documented
  the residual-dominates case and the `Σ CvR + residual == CV` tie-out.
- _Assumption badge isn't always present_ — **Fixed:** described the `assumptionBased: false`
  CV/SV-only fallback when the resource mix is unavailable.
- _Attribution limited to live `EP-` packages with finite EV/AC/PV_ — **Fixed:** stated the
  scope limit in the API and guarantees sections.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
