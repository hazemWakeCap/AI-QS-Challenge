# QS Cost Early-Warning

A **ranked watchlist** that flags a GREEN cost centre **one reporting period before** it tips to
AMBER, so a Quantity Surveyor acts while money is still unspent — plus a model-level validation
panel. ASP.NET Core 8 + React, with the EVM/drift analytics reimplemented in C# (no Python at
build or runtime).

Built to the reviewed plan at
[`../plan/idea-1-early-warning-classifier-implementation-plan.md`](../plan/idea-1-early-warning-classifier-implementation-plan.md).

## What it does

- Loads `9_HISTORICAL_DATA` (173 cost centres × 12 periods) via ClosedXML.
- Models the target **`AlertLevel(p+1) == "AMBER"`** on the GREEN-at-`p` population (117 real
  GREEN→AMBER transitions; on live rows this coincides with next-period `CPI < 0.95`).
- Ranks centres with a **frozen, transparent rule** (`RuleRiskScore@v1`) — a gap+CPI-proximity
  score, no black box. An ML.NET challenger is out of scope for the demo (stretch).
- Validates with **rolling-origin** back-testing (8 folds, origins 4–11), reported honestly as
  per-fold counts + fold range (no fragile CI). Result on this workbook:
  **precision@5 = 45%** vs **35%** for the best CPI-native baseline.
- Serves a **live forecast** for the latest period (12) from a separate all-history artifact, and
  **retrospective/out-of-fold** views for earlier periods — each period uses a model trained
  strictly on its past (no leakage).

## Run it

Prereqs: .NET 8 SDK, Node 18+ / pnpm. The workbook is read from `../data/Tower_X_Project_Data.xlsx`.

### Backend (API)

```bash
dotnet test                                   # 27 tests: data contract, pairs, leakage, metrics, lifecycle
dotnet run --project src/QsEarlyWarning.Web.API
# listens on http://localhost:5070
```

Endpoints:

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/health` | load state, row/centre counts, scorer version |
| `GET /api/v1/watchlist?period={4..12}&k={5\|10}` | ranked watchlist; 400 malformed, 404 no-artifact |
| `GET /api/v1/validation-summary` | frozen historical backtest (model-level, not per-period) |

### Frontend

```bash
cd frontend/qs-early-warning
pnpm install
pnpm dev            # http://localhost:5173, proxies /api → :5070
```

Set a different backend with `API_URL=http://host:port pnpm dev`.

## Architecture (layered, mirrors the WakeCap backend)

```
src/
  QsEarlyWarning.Domain/          entities, EvmSnapshot identities, EvmThresholds
  QsEarlyWarning.Infrastructure/  ExcelPanelLoader (ClosedXML) + schema validation
  QsEarlyWarning.Core/            FeatureBuilder, RuleScorer/RuleFitter, RollingOriginEvaluator,
                                  WatchlistScoringService, ModelProvider
  QsEarlyWarning.Web.API/         Watchlist / ValidationSummary / Health controllers
tests/QsEarlyWarning.Tests/       xUnit — validated against the real workbook
frontend/qs-early-warning/        React 18 + TS + Vite watchlist + validation panel
```

Data flow: workbook → raw panel → transition pairs (explicit `p→p+1` adjacency, exact-predecessor
lag features) → per-origin OOF rule artifacts + one cutoff-12 forecast artifact →
`WatchlistScoringService` (`artifactFor(period)`) → API → React.

## Key design decisions (from the plan review rounds)

- **The rule is the predeclared, deployed scorer** — nothing is selected on the eval folds, so the
  back-test is unbiased. The challenger, if added, is descriptive-only and never adopted.
- **`RuleRiskScore@v1` is frozen**: `0.7·clamp01((gap−x*)/gap_scale) + 0.3·cpiProximity`, where
  `cpiProximity = clamp01(1 − (Cpi − 0.95)/0.10)` is maximal at the 0.95 line (proximity-from-above,
  correct for a GREEN population where CPI ≥ 0.95). Only `x*` and `gap_scale` are fit, train-only.
- **Metrics are honest**: `precision@k = TP / min(k, eligible)`; zero-positive folds → recall N/A;
  KPIs are model-level historical, never shown as the selected period's live accuracy.
- **Provenance**: organiser-generated single-project workbook — exploratory evidence, no
  cross-project generalisation claim.

## Not built (stretch backlog, per plan §7)

ML.NET FastTree challenger (S1), the Microsoft Agent Framework copilot (S2), authenticated
`admin/reload` + observability (S3). The demo-critical path (rule watchlist + valid backtest + API
+ table) is complete and is the guaranteed deliverable.
