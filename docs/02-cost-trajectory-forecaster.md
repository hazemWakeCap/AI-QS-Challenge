# Feature 2 — Cost-Trajectory Forecaster

## What it is

A calibrated forecast of **next-period spend** for each cost centre, with a P10–P90
uncertainty band, plus a directional "cost cone" view of where the project is heading. It
answers *"how much are we about to spend, and how confident can we be?"*

## Who it's for

The QS doing cash-flow and commitment planning who needs a defensible next-period number, not
just last month's actuals extrapolated.

## How it works

- Forecasts **incremental (per-period) spend**, scored against realized `AC_AED_Period`
  (equivalently the difference between consecutive cumulative-AC values). It is **never**
  scored against cumulative AC or `EAC_AED` — those would be circular.
- Uses a ridge regression over leakage-safe features; `EAC`/`VAC`/`EAC_vs_BAC_Ratio` are never
  used as inputs.
- **Live forecasts are anchored at the latest origin only.** Earlier origins are used purely
  in the back-test, so the live number is never contaminated by future data.
- Produces a P10–P90 band so the QS sees the range, not a false-precision point estimate —
  **when calibration is sufficient**. If there isn't enough history to calibrate, the band is
  null and only the P50 is shown.
- The **cost-centres list** returns the immediate next period (horizon 1). The **cone**
  endpoint returns the fuller picture — horizons 1–3 plus the cumulative cone over time.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/forecast/cost-centres` | Per-centre next-period (horizon 1) spend forecast (+ band when calibrated). |
| `GET /api/v1/forecast/cone?bcc={id}` | One cost centre's horizon 1–3 increments + cumulative cost-cone over time. |
| `GET /api/v1/forecast/rollup` | Project-level rollup of the forecasts. |
| `GET /api/v1/forecast/backtest` | Back-test accuracy across historical origins. |

## UI

The **Forecast** tab: the forecast cone (`ForecastCone`) and the back-test panel
(`ForecastBacktest`).

## Guarantees & limits

- **Validated on incremental spend** — the next-period number is back-tested honestly.
- **Final cost is directional only** — median last-period progress is ~13% and only a handful
  of centres finish on this workbook, so there is no ground truth for a final-cost number. Any
  EAC shown from this feature is labelled directional, not validated.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _`/cone` requires `bcc`_ — **Fixed:** documented as `/cone?bcc={id}`, per-cost-centre.
- _P10/P90 band is not always produced_ — **Fixed:** now qualified as available only when
  calibration is sufficient (else P50-only).
- _Forecast covers horizons 1–3, not just h=1_ — **Fixed** (then refined in round 2).

**Round 2 (CHANGES REQUESTED) — resolved.**

- _`/forecast/cost-centres` returns only horizon 1; horizons 1–3 come from `/forecast/cone`_ —
  **Fixed:** cost-centres now documented as horizon 1 only, cone as horizons 1–3 + cumulative
  cone.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
