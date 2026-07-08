# Feature 7 — Live EVM Dashboard

## What it is

The always-on read-side view of the project's cost health: EVM totals for the selected period,
the period-by-period trend, and a per-cost-centre grid. It's the landing view — the "how are we
doing right now?" screen.

## Who it's for

The QS or project controls lead who opens the app to check the current state before drilling
into warnings or forecasts.

## How it works

- The **overview** and **cost-centres** reads serve **computed EVM** for the selected project
  straight from Postgres — the budget/EV/AC and derived CPI/SPI/CV/SV are computed by the
  platform, not looked up (the source workbook intentionally omits the computed EVM sheets).
- Two views:
  - **Overview** — project EVM totals for a period + the full period-by-period trend.
  - **Cost centres** — the same EVM computed per cost centre for the selected period (the grid).
- Both accept an optional `period` query parameter; project-level CPI is always
  `sum(EV)/sum(AC)`, never the mean of per-row CPIs.
- **`/health` is different**: it is global and unauthenticated, and reports the process's startup
  workbook model (row/centre counts, scorer version) — not the selected project's Postgres data.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/overview?period={p}` | Project EVM totals for a period + period-by-period trend (Postgres, per project). |
| `GET /api/v1/cost-centres?period={p}` | Per-cost-centre computed EVM for a period (Postgres, per project). |
| `GET /api/v1/health` | Global, unauthenticated process/model health: startup workbook row/centre counts, scorer version. |

## UI

The **EVM Overview** tab (`EvmOverview`) and the **Cost Centres** tab (`CostCentreGrid`).

## Guarantees & limits

- **Derived, not looked up** — the overview and cost-centre EVM figures are computed by the
  platform from Postgres, so they're consistent with the analytics features. (`/health` is the
  exception — it's the global workbook-backed process/model health, not project EVM.)
- **Read-only** — the dashboard never mutates; changes come through the
  [Authoring Workflow](08-authoring-workflow.md).

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _`/overview` and `/cost-centres` accept optional `period`_ — **Fixed:** documented `?period={p}`.
- _`/health` is global/unauthenticated, workbook-backed, not selected-project load state_ —
  **Fixed:** described as global process/model health.
- _"All EVM computed straight from Postgres" is too broad_ — **Fixed:** scoped the Postgres claim
  to overview + cost-centre reads and separated `/health`.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
