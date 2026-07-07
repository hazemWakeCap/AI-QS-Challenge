# Plan — Integrate Idea 2 (short-horizon incremental-spend forecaster) into the platform

> Companion to `digitalization-postgres-platform-plan.md`. That plan built the Postgres system of
> record + dashboard; this plan adds Idea 2's calibrated cost forecaster on top of it.

## Context

Idea 2 (`ideas/idea-2-eac-forecaster.md`) replaces the whipsawing `EAC = BAC ÷ CPI` with a **calibrated
short-horizon incremental-spend forecast** (horizons h=1,2,3, predicted in cost space), shown as a
P10/P50/P90 cost cone (a **nominal 80% prediction interval**) that tightens with progress, plus a
*directional* final-cost cone. Its honest, validatable target is next-period incremental AC
(`AC(k)−AC(k−1)`), not final cost (only 4/173 centres finish in-window, median 13% complete). The idea's
original deliverable was a standalone Python/Streamlit app; this plan **integrates it into our existing
.NET + React + Postgres platform** so the QS sees the cone inside the same dashboard, backed by the same
tenant/RLS boundary and project snapshot.

**Decisions (confirmed with the user):** forecaster in **C# in `QsEarlyWarning.Core`** (one stack; borrows
the *adjacency* concept from `FeatureBuilder` and the *expanding-prefix pattern* from
`RollingOriginEvaluator`, but owns its own increment helper and multi-horizon walk — see Reuse note below);
**closed-form ridge** for the P50 (no ML dependency; bands come from held-out residual quantiles);
**full UI** (per-centre cone + trust badge + project short-horizon spend-scenario roll-up + back-test panel
+ subordinated final-cost cone).

## Data & leakage guards (non-negotiable)

- Source: the project **snapshot panel** (`ProjectSnapshot.Panel : IReadOnlyList<CostCentrePeriod>`),
  already loaded via `PostgresPanelLoader`. Per centre, periods are ordered; `AcCumulative` is cumulative.
- **Derive** per-period increment `Δk = AcCumulative(k) − AcCumulative(k−1)` with the forecaster's **own**
  exact-predecessor helper (borrow the adjacency idea from `FeatureBuilder.Delta` but do not reuse it — it
  is `private` and embeds alert-status rules; the forecaster ships `IncrementHelper` and never differences
  across a period gap). Label at horizon h = `Δ(k+h)`; this label belongs to **target period `k+h`**.

- **Availability is defined on the TARGET period, not the feature period.** A training row for horizon `h`
  built at feature period `k` (origin `o`) is usable **only if its label/target period `k+h` is strictly
  before `o`** (`k+h < o`). Features may use periods `≤ k`, but the *label* must resolve to an already-observed
  period. The correct rule is: **features from periods ≤ k AND label period `k+h` < origin.** This prevents
  h=2/3 labels from leaking future observations. Calibration residuals obey the same rule: a residual is
  eligible only if its **target period `k+h` < evaluation origin `o`**.

- **Derive rolling-3M CPI** in the forecaster from **rolling sums of incremental EV and AC** over the ≤3
  present preceding periods — `rollCPI(k) = Σ ΔEV / Σ ΔAC` (sum-of-increments, *not* an average of per-period
  ratios, which is unstable near-zero) — the Postgres panel leaves `Rolling3mCpi` null. **The current period
  `k` IS included** in the window (features are period-≤k); state this in code and the DTO so back-test and
  live agree.

- Features strictly from periods ≤ k: recent increment(s), **planned increment** `ΔPV(k) = PvAed(k) −
  PvAed(k−1)` (or `Δplan%(k) × BacAed`) — **never cumulative `plan% × BAC`, which double-counts prior
  spend**; derived rolling-CPI (sum-of-increments as above); progress (`ActualPctComplete`), `BacAed`,
  resource-share mix.
  **Never use `EacAed`/`VacAed`/`EacVsBacRatio` as features** (they equal `BAC/CPI` — leak the baseline).

- **Schedule-vintage assumption (planned-spend / CPI baselines).** This project has a **single static plan
  curve** — `cost_centre_plan_periods` is fixed per published version and is never revised mid-project — so
  `ΔPV(k+h)` at any historical origin `o` is the plan **as known at origin** (`PvAed` is not retrospectively
  revised). There is therefore **no schedule-vintage leakage** in the planned-spend / CPI baselines here. Note
  for portability: with **revised schedules**, these baselines would have to read the **as-of-origin plan
  vintage** (the plan published before `o`), not the latest curve.

- **Two disjoint tiers — frozen constants vs fold-local fitted statistics.**
  - **Frozen constants (declared once, up front; NEVER estimated per fold, NEVER touched by any reported
    figure).** These are configuration, not fitted quantities: ridge `λ` (default **λ = 1.0** on standardized
    features, plus the ridge-floor retry schedule λ → 10, 100), **progress-bin edges = [0, 10, 25, 50, 100]%**,
    **`minCount` = 60** (minimum calibration residuals in a bin before its own quantiles are trusted), the
    **progress gate ≈ 10%** (below it a centre is "too early"), the **early-progress claim band `<40%`**, the
    **centre-group split rule** (`hash(BccId) mod 10`, buckets 0–6 → proper-training / 7–9 → calibration,
    ≈70/30; `K = 10` folds for cross-fitting), and the **minimum viable fold sizes** (`minTrainRows = 40`,
    `minCalRows = 30`). They are frozen in code before the back-test runs and are identical across every fold and in production.
    Do **not** describe any of these as "computed from proper-training rows" — they are not estimated at all.
  - **Fold-local fitted statistics (genuinely estimated, only from the fold's proper-training rows at origin
    `o`).** The only things fit from **proper-training** rows are: feature standardization **mean/σ** and
    missing-value **imputation values**. They are computed only from proper-training rows eligible under the
    target-period rule (`target period < o`) and then applied to calibration/evaluation rows; no fitted
    statistic ever sees data at or after `o`.
  - **Residual store — NOT computed from proper-training rows.** The residual store is a fold-local artifact
    but is **populated exclusively from calibration rows (back-test, construct a) / out-of-fold rows (serving,
    construct b) — never from a model's own proper-training rows** (a model must never score the rows it was
    fit on for residuals). Those calibration/OOF rows are still gated by the target-period rule
    (`target period < o`). **Calibration residuals are never clipped** (see interval method) — clipping would
    invalidate coverage.

- Grouped **rolling-origin** (own the walk; do not reuse `RollingOriginEvaluator`, which is a single-horizon
  pattern): at each origin `o`, split eligible rows into a **proper-training** set and a **calibration** set
  (see Core module for the concrete split), fit the P50 on proper-training, collect calibration residuals,
  evaluate at `o`. Both proper-training and calibration rows must satisfy `target period < o`.

## Core forecasting module — `src/QsEarlyWarning.Core/Forecasting/` (new)

- `IncrementHelper.cs` — the forecaster's **own** exact-predecessor increment/lag helper (not the private
  `FeatureBuilder.Delta`). Public, no alert-status coupling.
- `ForecastFeatureBuilder.cs` — per-centre feature/label extraction. Emits
  `IncrementSample { BccId, FeaturePeriodId, TargetPeriodId(h), Progress, double[] Features, double? LabelH1/H2/H3, Bac }`.
  `TargetPeriodId(h)` is carried on every sample so eligibility can be checked on the target period.
- `RidgeRegressor.cs` — closed-form ridge, hand-rolled, no dependency. **Standardize features on the
  proper-training rows only** (store mean/σ, apply to predict inputs); **unpenalized intercept** (do not
  shrink the bias); solve via **QR or Cholesky** (not raw normal equations, which are numerically weak).
  Explicit **failure handling**: on non-SPD/rank-deficient factorization, increase `λ` (ridge floor) and
  retry, then fall back to the run-rate baseline for that horizon and flag it. **Missing values** imputed to
  the fold-local training mean (post-standardization → 0) with a companion "was-missing" indicator feature.
  `Fit(double[][] X, double[] y, double lambda)`, `Predict(double[] x)`. One per horizon.
- `IncrementalSpendForecaster.cs` — `Fit(panel, ReportingOrigins)`: per horizon, fits a ridge P50 and builds
  a held-out **residual store keyed by progress bin**.
  - **Interval method — two distinct constructs (do NOT conflate them).**
    - **(a) Back-test conformal (per fold, self-contained).** Each rolling-origin fold is fully
      self-contained: its residual scores come from **that fold's own calibration partition, scored under
      that fold's own fitted ridge**. Concretely, at origin `o`, eligible rows (target period < `o`) are
      split by centre-group (a centre's rows fall entirely on one side) into a **proper-training** set (fits
      that fold's ridge) and a disjoint **calibration** set; the calibration rows are scored under *that same*
      fitted model to produce signed residuals `r = y − ŷ`. **Residuals are never pooled across folds / across
      different-origin models** — each fold builds its interval from its own model + its own calibration
      residuals, and coverage is measured per fold, then aggregated (§Verification). Within a fold, residuals
      are grouped by the frozen progress bins.
      - **Deterministic split (frozen, declared before evaluation).** A centre's side is fixed by
        `hash(BccId) mod 10`: buckets `0–6` → **proper-training (~70%)**, buckets `7–9` → **calibration
        (~30%)**. The hash is a stable, seedless function of the string `BccId` (e.g. FNV-1a), so the
        assignment is **identical across every fold and every run** and is declared before any figure is
        seen — never chosen after results. A centre is on the same side in every fold in which it is eligible.
      - **Minimum viable fold sizes.** If a fold's proper-training count `< minTrainRows = 40` **or** its
        calibration count `< minCalRows = 30` (frozen constants), that fold is **skipped**: its intervals are
        marked unavailable and it is **excluded from aggregated coverage**, with the drop count + reason
        logged in `ForecastValidationSummary`. Skipped folds never contribute residuals to any other fold.
    - **(b) Production / live residual store — cross-fitted (out-of-fold), NOT in-sample.** The serving store
      must reflect genuine out-of-sample uncertainty, so residuals are **never** scored on rows the serving
      model trained on. Chosen approach: **cross-fitted (out-of-fold) residuals.** Partition all eligible rows
      (through the latest origin) by the **same centre-grouped K-fold split** used elsewhere (`K = 10`,
      `hash(BccId) mod 10` → fold id, frozen), so a centre's rows sit entirely in one fold. For each fold `f`,
      fit a ridge on the other `K−1` folds and score fold `f`'s rows under *that* model to collect their
      **OOF residuals**; the union of these OOF residuals (each from a model that did **not** train on that
      row) is the serving residual store, grouped by the frozen progress bins. **Then refit the serving P50
      on all eligible rows** — this all-data fit is used only for the **point prediction**; the interval
      half-widths come from the OOF residual store. The live intervals `ForecastCentre` serves therefore
      reflect out-of-sample uncertainty, not the (optimistically narrow) in-sample spread. This store is
      deliberately separate from the back-test folds' residuals and is never used to compute reported coverage.
      (Alternative, if cross-fitting is later deemed too costly: a **fixed centre-grouped production
      calibration partition** — e.g. `hash(BccId) mod 10 ∈ {7,8,9}` — held out of the serving fit, with the
      serving P50 fit on the complement and residuals scored only on the held-out partition. Cross-fitting is
      preferred because it uses all centres for the residual store.)
    - **Finite-sample quantiles (both constructs).** For a nominal 80% interval (α = 0.20) with `n` calibration
      residuals in the applicable progress bin, order the residuals and take lower = ⌊(n+1)·(α/2)⌋-th and
      upper = ⌈(n+1)·(1−α/2)⌉-th order statistic. **If a rank falls outside `[1, n]`** (small `n`), do **not**
      clamp it into range — instead use an **unbounded endpoint** for that side (−∞ lower / +∞ upper). If the
      pooled calibration set is below `minCount` even after falling back to the all-bins residual set, mark the
      interval **unavailable** and set the trust badge to "too early / insufficient calibration" rather than
      emitting an under-covering interval. Fallback order for a sparse bin: bin-local residuals → pooled
      (all-bins) residuals → unavailable. Endpoints are reported as a **nominal 80% prediction interval**
      (coverage is then *measured*, §Verification — not assumed).
  - `ForecastCentre(history, k)` → per-h `{ incrementP10, P50, P90 }` **plus a cumulative cone built by joint
    residual simulation** (§below), not by summing endpoints; + `TrustBadge` (validatable vs "too early":
    progress-gate + calibration-analog count). **Live serving fixes `k` to the centre's latest present period
    (the forecast origin)** — the genuine next-period forecast. Serving a historical `k` is refused: the
    serving P50 + OOF residual store are fit through the latest origin, so producing a forecast for an earlier
    `k` would use labels from periods `> k` (future leakage). Historical origins are forecast **only** inside
    the back-test, which does proper as-of-origin fits (each fold trained only on target period `< o`). *(Future
    work, option b: an as-of-`k` fit + OOF store trained only on rows with target period `< k`, if per-period
    historical serving is ever needed.)*
- **Cumulative cone — joint residual simulation (NOT endpoint summing).** Summing marginal `ΣP10_h`/`ΣP90_h`
  is invalid because horizon errors are dependent. Instead draw **joint residual vectors** `(r_{h=1}, r_{h=2},
  r_{h=3})` from the held-out calibration set (each draw is one centre-origin's actual residual *path* across
  the three horizons, preserving their empirical correlation), form the cumulative path
  `AC_k + Σ_{j≤h}(P50_j + r_j)`, and take the P10/P50/P90 **quantiles of the simulated cumulative
  distribution** at each h. (Equivalently, calibrate cumulative-horizon error directly; the path-draw is the
  chosen route.)
  - **Complete-case rule (draws must be genuine paths).** Each per-horizon residual store keys residuals by a
    shared **centre-origin key `(BccId, FeaturePeriodId)` under the same fold** (the stores expose this key so
    the intersection is well-defined). Joint paths are drawn **only from keys that have all three (h=1,2,3)
    residuals eligible under the same fold** — the **complete-case intersection** of the three horizon stores.
    A key missing any horizon (e.g. `k+h ≥ o` for the longer horizons) is excluded; horizons are never mixed
    across different keys or folds. **Fallback when complete paths are scarce in a progress bin:** fall back to
    **pooled complete paths** (all-bins complete-case intersection); if still below `minCount`, the cumulative
    cone is marked **unavailable** (trust badge → "insufficient joint calibration") rather than fabricated. The
    marginal per-horizon increment intervals remain available even when the cumulative cone is unavailable.
- `ForecastBaselines.cs` — the four baselines for the back-test: zero-increment/random-walk, planned-spend
  (`ΔPV`), recent-run-rate, and a **CPI baseline** that inflates planned spend by the rolling cost
  performance. With **`CPI = Σ ΔEV / Σ ΔAC`** over the rolling window, planned work `ΔPV` costs more than plan
  when `CPI < 1`, so the predicted incremental **cost = `ΔPV(k+h) ÷ CPI`** (dividing, *not* multiplying —
  cost-to-do = planned-value-to-do ÷ efficiency) — *not* cumulative `BAC/CPI`, and *not* `ΔPV × CPI`.
  **Fallback when `CPI` is zero, undefined, or non-finite** (e.g. `Σ ΔAC = 0`, or CPI ≤ 0): fall back to the
  planned-spend baseline (predicted increment = `ΔPV(k+h)`) and set a flag on that prediction so the back-test
  shows the fallback rate. Echoes the `CpiNativeScorers` "baselines side-by-side" idea.
- `ForecastMetrics.cs` — **new** regression/interval metrics (Metrics.cs has only ranking): `Mae`,
  `MaePctOfBac`, `Wape`, `IntervalCoverage`, reliability bins. **Aggregation is defined precisely:**
  `MaePctOfBac` = **mean over centres of `|error| / BacAed`** (per-centre normalized error, then averaged);
  `Wape` = **global `Σ|error| / Σ|actual|`** (they differ — both reported). `IntervalCoverage` returns the
  achieved fraction **with `n` and a binomial (Wilson) uncertainty interval**. `MapeGated` (only past a
  min-progress gate).
- `ForecastEvaluator.cs` — grouped rolling-origin back-test → `ForecastValidationSummary` (per-horizon:
  P50 MAE-%BAC / WAPE for the model and each baseline, **on the identical set of eligible rows**, + measured
  P10–P90 coverage with `n` + binomial band + reliability), reported both overall and cut by the early
  progress band. **It reports the comparison; it does not require the model to win.** Fold counts and sample
  counts are published alongside every figure.
- `ForecastModels.cs` — records: `ConePoint`, `CentreForecast`, `ProjectSpendScenario`, `HorizonMetric`,
  `ForecastValidationSummary`.
- **Project roll-up — short-horizon spend scenario ONLY.** Draw each centre's next-period (h=1, extendable to
  the 3-period window) increment as an **empirical residual draw** around its P50 (`P50 + r`, `r` sampled from
  that centre's held-out residuals — a *scenario* draw, not a probability), sum across centres, repeat to get
  a **spend-scenario distribution** (P10/P50/P90 of near-term project spend). **No total-cost band and no
  over-budget probability** (those would promote the unvalidated final-cost extrapolation). Caveats surfaced:
  the sum assumes centre independence and therefore **understates common project shocks** (weather, escalation,
  design change) that hit centres together; and the scenario spread is not a calibrated probability of any
  budget outcome.

## Registry integration — `src/QsEarlyWarning.Core/Registry/ProjectSnapshotRegistry.cs`

Extend `ProjectSnapshot` with `Forecast` (the fitted `IncrementalSpendForecaster` + its
`ForecastValidationSummary`), built in `Build(...)` right after `new RollingOriginEvaluator().Train(panel)`
so it is cached per project on the same in-memory snapshot, no extra DB path. **`ProjectSnapshotRegistry`
has no change detection** — it does *not* rebuild when underlying data changes; it rebuilds a snapshot only
on an **explicit `RebuildAsync`** request (keeping the same last-known-good/dedup semantics). So: the
forecaster is **built at snapshot `Build()` and refreshed via `RebuildAsync`** — never "on data change".

## API — `src/QsEarlyWarning.Web.API/Controllers/ForecastController.cs` (new), route `api/v1/forecast`

Reuse `DashboardController`'s tenant resolution (`IProjectSnapshotRegistry` + `ProjectDirectory` +
`TenantContext`; 401/403/404). New DTOs in `Contracts/ForecastDtos.cs` (positional records, existing style):
- **Live endpoints serve the latest origin only.** The forecaster's serving fit (P50 + OOF residual store) is
  built through the project's latest present period, so all live forecasts are anchored there — the genuine
  "what happens next" forecast. **Historical-origin forecasts are NOT served live** (they would leak future
  data); historical origins are evaluated only in `/forecast/backtest` via proper as-of-origin fits.
- `GET /forecast/cost-centres` → list `{ bccId, discipline, progress, trust, nextP10/P50/P90 }` (picker/overview),
  anchored at each centre's latest present period.
- `GET /forecast/cone?bcc=` → `CentreForecastDto`: per-h increment interval (nominal 80%) +
  cumulative cone series from joint residual simulation (P10/P50/P90 over BAC/PV), trust badge, and the
  subordinated directional final-cost cone — **anchored at the centre's latest present period (the forecast
  origin)**. An arbitrary historical `period=` is **dropped/ignored for live serving** (at most accepted to
  select the centre's latest available anchor); it never re-anchors the forecast to a past origin.
- `GET /forecast/rollup` → `ProjectSpendScenarioDto`: **short-horizon project spend-scenario
  distribution** (P10/P50/P90 of near-term spend) with the independence + not-a-probability caveats. **No
  over-budget probability, no total-cost band.**
- `GET /forecast/backtest` → `ForecastValidationSummaryDto` (mirrors `ValidationSummaryController`): the
  credibility artifact — MAE-%BAC/WAPE per horizon **for the model and each of the 4 baselines on identical
  eligible rows** + measured coverage (with `n` + binomial band) + reliability + fold/sample counts.
  Reports the comparison as-is (may show no model win).

## Frontend — new "Forecast" tab

- `src/api/client.ts`: add `forecastCostCentres/forecastCone/forecastRollup/forecastBacktest` + types.
- `App.tsx`: add a `forecast` tab.
- `components/ForecastCone.tsx` — centre picker + **cost-cone SVG**: extend the existing `Spark`
  (EvmOverview.tsx) with a shaded band via an SVG `<polygon>` (P90 forward + P10 reversed) behind the P50
  line, overlaid on BAC/PV; trust badge; a toggle that reveals the de-emphasised final-cost cone. **The cone
  forecasts forward from the centre's latest present period** — there is **no historical-origin forecasting in
  the UI** (the picker chooses a centre, not a past origin); prior periods are shown as observed actuals only.
- `components/ForecastBacktest.tsx` — mirrors `ValidationPanel.tsx`: per-horizon MAE-%BAC/WAPE for the model
  and the 4 baselines (identical rows) + a coverage readout showing achieved coverage, `n`, and the binomial
  band, with the "measured, not asserted — may show no win" framing.
- Project **spend-scenario** card on the tab: P10/P50/P90 of near-term project spend, labelled a scenario
  distribution (not a probability) with the centre-independence / common-shock caveat. No over-budget %.

## Verification

**Frozen before any evaluation.** All tunable choices — ridge `λ` (and its ridge-floor retry schedule),
the progress gate, the early-progress claim band (`<40%`), the feature set, the residual/progress bins and
`minCount`, and all fallbacks — are **fixed constants declared once and frozen before the back-test runs**.
No figure reported by `ForecastEvaluator` may be used to retune them. (If tuning is later wanted, the only
sanctioned route is **nested rolling-origin tuning inside each fold plus one final untouched temporal
block** held out from all tuning — chosen approach for this plan: **freeze, no tuning on reported folds**.)

Tests **verify calculations and leakage constraints — they do not assert a model win.** A legitimate
outcome may show a baseline matching or beating the model; the suite reports the comparison, it does not
gate on it.

1. **Unit tests** (`tests/QsEarlyWarning.Tests`, xUnit like the existing 34), assert:
   - increment derivation matches `AC(k)−AC(k−1)` and never differences across a period gap;
   - **target-period availability**: for a synthetic panel, every training/calibration row used at origin
     `o` has label/target period `k+h < o` (no future-label leakage at h=2/3);
   - features never include `EacAed`/`VacAed`/`EacVsBacRatio`;
   - **frozen vs fold-local split holds**: the frozen constants (`λ`, progress-bin edges `[0,10,25,50,100]%`,
     `minCount=60`, gate≈10%, `<40%` band) are literal constants — identical across all folds and unchanged
     by any input perturbation; the proper-training-fitted statistics (standardization mean/σ, imputation
     values) computed at origin `o` depend only on proper-training rows with target period `< o` (perturbing a
     period `≥ o` leaves them unchanged); the **residual store excludes proper-training rows** — every residual
     is scored on a calibration (back-test) / out-of-fold (serving) row, never on a row its model was fit on;
     **calibration residuals are never clipped**;
   - planned-increment uses `ΔPV`/`Δplan%×BAC` (not cumulative `plan%×BAC`); rolling CPI = `ΣΔEV/ΣΔAC`
     with the current period included;
   - the cumulative cone comes from joint residual simulation (not endpoint summing) — a rigged
     correlated-residual fixture makes the two disagree and the sim path is used; **joint paths are drawn
     complete-case** (only centre-origin keys with all three horizon residuals under the same fold; a key
     missing h=3 is excluded), and the cone is marked **unavailable** when complete paths fall below `minCount`
     even after the pooled fallback (marginal per-horizon intervals stay available);
   - **serving (live) residuals are out-of-fold, never in-sample**: on a fixture, every residual in the
     production store is scored by a model that did not train on that row (cross-fitted), while the serving
     P50 point prediction uses the all-data refit; perturbing a row shifts only its own OOF residual's model,
     not the fold it was scored under;
   - **the centre-group split is deterministic and frozen**: `hash(BccId) mod 10` puts each centre on the same
     proper-training/calibration side across all folds and runs (≈70/30), and a fold whose proper-training
     `< 40` or calibration `< 30` is **skipped** (intervals unavailable) and **excluded from aggregated
     coverage** with the drop logged;
   - `MaePctOfBac` (mean per-centre `|err|/BAC`) and `Wape` (global) are computed per their definitions;
   - **per-fold conformal uses that fold's own calibration partition** scored under that fold's own fitted
     model (residuals are not pooled across different-origin models); **coverage is computed and reported**
     with `n` and a binomial (Wilson) interval for the model on OOF rows (reported, not asserted to hit 80%);
   - **finite-sample endpoints**: when an order-statistic rank falls outside `[1,n]` the interval side is
     **unbounded (±∞)**, and below `minCount` (after the all-bins fallback) the interval is **unavailable**
     with a "too early / insufficient calibration" badge — never a clamped/under-covering interval;
   - the **CPI baseline predicts `ΔPV ÷ CPI`** (not `ΔPV × CPI`) and falls back to `ΔPV` (flagged) when CPI is
     zero/undefined/non-finite;
   - **residual stores exclude proper-training rows**: on a fixture, no residual in any store (back-test
     calibration or serving OOF) is scored by the model that trained on that row; perturbing a proper-training
     row changes standardization mean/σ and imputation values but adds no new residual sourced from it;
   - **live serving refuses / does-not-leak historical origins**: `ForecastCentre` (and the `/forecast/cone`
     endpoint) always anchor at the centre's latest present period; a request naming a historical `period=`
     either forecasts from the latest origin or is refused — it never produces a forecast anchored at a past
     `k` (which would use labels from periods `> k`);
   - the back-test emits model + all 4 baselines on the **identical eligible row set** with fold/sample
     counts. Keep existing gates green.
2. Build: `dotnet build QsEarlyWarning.sln`; `cd frontend/qs-early-warning && npx tsc --noEmit`.
3. Run API (`:5070`) + dashboard (`:5173`) against `qs_phase1`; curl `/forecast/backtest` (model-vs-baseline
   table with counts + coverage), `/forecast/cone?bcc=BCC-ARC-MAS-301` (interval tightens with progress),
   `/forecast/rollup` (spend-scenario, no over-budget %); confirm tenancy (non-member → 403).
4. Browser (Playwright MCP): open the **Forecast** tab, pick an early/noisy centre and a further-along one,
   confirm the cone + interval + trust badge render and the back-test panel shows the measured model-vs-
   baseline comparison and coverage readout (whatever the result); screenshot.

## Out of scope / notes (honoring the idea's guardrails)

- Final-cost cone stays **directional and subordinated** (4/173 completers — no ground truth); the headline
  is the validated 1–3-period band.
- Learned quantile/GBM ensemble is **deferred** (can't calibrate on 4 completers); the nominal-80% residual
  intervals are honest now.
- **Reported coverage/metrics are for temporal OOF predictions only.** Because the same centres recur across
  rolling-origin folds, the back-test evidences **within-project temporal generalization**, *not*
  new-centre/new-project generalization — stated on the back-test panel.
- Project spend-scenario roll-up assumes centre independence and understates common project shocks (stated
  caveat); correlation modelling deferred. It is a **short-horizon spend scenario**, never a final-cost band
  or over-budget probability.
- Optional later: expose `ForecastCentre` as a **copilot tool** (idea 4) so "what's my forecast for BCC-X?"
  answers in words — not part of this plan.

## Codex Review — round 1 (2026-07-07)

### Blocking findings
1. **Horizon-label leakage.** At origin `o`, training must not include rows merely because feature
   period `k < o`; it needs `k+h < o` (the label period must be strictly before the origin), else h=2/3
   labels come from the future. Availability must be defined on the **target period**, not the feature
   period. Calibration residuals must likewise have `target period < evaluation origin`.
2. **"Split conformal" is under-specified.** Expanding earlier residuals + progress bins + a changing
   model is not ordinary split conformal, and signed-residual P10/P90 quantiles don't guarantee
   finite-sample 80% coverage. Specify a fixed proper-training/calibration split per fold, or an explicit
   prequential/online conformal algorithm, with finite-sample quantile correction.
3. **Summing marginal interval endpoints is invalid.** `AC_k + ΣP10_h` / `AC_k + ΣP90_h` is not an 80%
   cumulative cone (horizon errors are dependent). Calibrate cumulative-horizon error directly, or
   simulate joint residual vectors.
4. **The roll-up claim is incoherent.** A 1–3-period model cannot yield a "total-cost band" / over-budget
   probability without promoting the explicitly-unvalidated final-cost extrapolation. Restrict the roll-up
   to short-horizon spend, or drop the probability/total-cost language.
5. **A conformal interval is not a sampling distribution.** "Sample from its conformal band" is undefined;
   uniform sampling fabricates probability. Use empirical residual draws and call it a *scenario*
   distribution; note centre independence understates common project shocks.
6. **The verification gate hard-codes the conclusion.** Requiring "P50 beats all four baselines" + UI
   confirmation of "the win" makes the back-test acceptance-driven. Tests must verify calculations and
   leakage constraints; a legitimate result may show no win.
7. **No honest untouched evaluation remains.** λ, progress gate, the `<40%` claim band, feature set,
   residual bins and fallbacks can all be tuned on the reported folds. Freeze them before evaluation, or
   use nested rolling-origin tuning + a final untouched temporal block.

### Recommendations
- Define availability by `TargetPeriod`; calibration residuals also `TargetPeriod < origin`.
- Report coverage only for temporal OOF predictions; state centres recur across folds (not new-centre/
  new-project generalization).
- Predeclare progress bins + minimum calibration counts; fall back to pooled calibration for small bins;
  expose `n`, achieved coverage, binomial uncertainty; call endpoints a **nominal 80% prediction
  interval**, not "calibrated P10/P90".
- Make ALL preprocessing fold-local (scaling, imputation, clipping, λ, bin/analog thresholds).
- Fix planned-increment semantics: `plan%×BAC` is cumulative — use `ΔPV` or `Δplan%×BAC`; define the CPI
  baseline from **incremental** planned EV/PV, not cumulative.
- Derive rolling CPI from rolling sums of incremental EV and AC (or justify averaging ratios); state
  whether the current period is included.
- Closed-form ridge OK at this scale, but standardize on training data, add an unpenalized intercept,
  define missing-value handling, use QR/Cholesky with explicit failure handling (normal equations are
  numerically weak).
- Reuse is overstated: `FeatureBuilder.Delta` is **private** and embeds alert-status rules (only its
  adjacency concept is reusable); `RollingOriginEvaluator` is a *pattern*, not a reusable multi-horizon
  walk. "Rebuilt on data change" is **false** — `ProjectSnapshotRegistry` rebuilds only on explicit
  request (no change detection).
- Define aggregation precisely (mean per-centre `|error|/BAC` vs global WAPE differ); publish fold counts,
  sample counts, and baseline metrics on identical eligible rows.

### Codex round-1 reconciliation

| # | Finding | Resolution | Now in section |
|---|---------|------------|----------------|
| 1 | Horizon-label leakage | Availability defined on the **target period**: a row for horizon `h` at origin `o` is usable only if label period `k+h < o` ("features ≤ k **AND** label period < origin"); calibration residuals also require target period < origin. | Data & leakage guards |
| 2 | Split-conformal under-specified | Concrete fixed **proper-training vs calibration split per rolling-origin fold**, pooled by progress bin, with **finite-sample conformal quantiles** (⌊(n+1)α/2⌋, ⌈(n+1)(1−α/2)⌉); endpoints called a **nominal 80% prediction interval**. | Core module (interval method) |
| 3 | Invalid summing of endpoints | Cumulative cone built by **joint residual-path simulation** across h=1..3 (empirical correlation preserved), then cumulative quantiles — never `ΣP10/ΣP90`. | Core module (joint residual simulation) |
| 4 | Incoherent roll-up | Roll-up restricted to a **short-horizon spend-scenario distribution**; "total-cost band" and "over-budget probability" removed from Core, API, and UI. | Core roll-up / API / Frontend |
| 5 | Conformal ≠ sampling distribution | Roll-up uses **empirical residual draws** (`P50 + r`) producing a **scenario** distribution (not a probability); centre-independence + common-shock understatement caveats kept. | Core roll-up / Out-of-scope |
| 6 | Verification hard-codes the win | Tests verify **calculations + leakage constraints** (increment derivation, target-period availability, fold-local preprocessing, coverage computed with `n` + binomial band); **no "P50 beats all baselines" assertion** — comparison reported, win not required. | Verification |
| 7 | No untouched evaluation | All hyperparameters (`λ`, progress gate, `<40%` band, feature set, bins, fallbacks) **frozen before evaluation** (chosen route); nested rolling-origin tuning + final untouched temporal block noted as the only alternative. | Verification (Frozen-before-evaluation) |
| R | Recommendations | Fold-local preprocessing; `ΔPV`/`Δplan%×BAC` (not cumulative) + incremental CPI baseline; rolling CPI = `ΣΔEV/ΣΔAC` with current period included; standardized ridge + unpenalized intercept + QR/Cholesky with failure handling + missing-value rule; reuse corrected (`FeatureBuilder.Delta` private/alert-coupled → own `IncrementHelper`; registry rebuilt at `Build()` / `RebuildAsync`, not "on data change"); aggregation defined (mean per-centre `|err|/BAC` vs global WAPE) on identical rows with counts. | Data guards / Core module / Registry |

## Codex Review — round 2 (2026-07-07)

### Remaining blocking findings
1. **Frozen params contradict fold-local computation.** `λ`, progress-bin edges, and `minCount` are
   described as BOTH computed from proper-training rows AND frozen constants. Pick one explicit rule: for
   the frozen route, **declare them as constants and never estimate them per fold**. (Keep genuinely
   fitted statistics — standardization mean/σ, imputation values, the residual store — fold-local; but the
   hyperparameters `λ`/bins/`minCount`/gate are frozen constants, not per-fold-estimated.)
2. **Conformal construction still ambiguous — must use each fold's own calibration partition.** Split
   conformal requires each evaluation fold's residual scores to come from *that fold's* calibration
   partition under *that fold's* fitted model. The plan appears to pool residuals across different
   rolling-origin models. Specify the back-test conformal (per-fold, own model + own calibration split)
   **separately** from the single production residual store used for live serving.
3. **Finite-sample correction is invalid for small `n`.** Clamping out-of-range order-statistic ranks to
   `[1,n]` can under-cover. Use **±∞ endpoints** when the rank falls outside the sample (unbounded
   interval), or mark the interval **unavailable** until the pooled calibration set is large enough. Also
   **do not clip calibration residuals** — clipping invalidates coverage.
4. **CPI baseline is inverted.** With `CPI = ΔEV/ΔAC`, predicted incremental cost is `ΔPV / CPI`, **not**
   `ΔPV × CPI`. Replace the formula and define the zero/invalid-CPI fallback behavior.

### Codex round-2 reconciliation

| # | Finding | Resolution | Now in section |
|---|---------|------------|----------------|
| 1 | Frozen params vs fold-local contradiction | Split into **two disjoint tiers**: **frozen constants** (`λ=1.0` + retry 10/100, progress-bin edges `[0,10,25,50,100]%`, `minCount=60`, gate≈10%, `<40%` band) declared once and **never estimated per fold**; only **standardization mean/σ, imputation values, and the calibration residual store** stay fold-local. "Computed from proper-training rows" wording removed from λ/bins/minCount/gate. | Data & leakage guards (two-tier bullet); Verification (frozen-vs-fold-local test) |
| 2 | Conformal must use each fold's own calibration | Interval method now names **two distinct constructs**: (a) **back-test conformal** — per fold, residuals from *that fold's* calibration partition scored under *that fold's* fitted model, **never pooled across origins**, coverage measured per fold then aggregated; (b) **production/live residual store** — a single store from the final full fit for serving. | Core module (interval method a/b) |
| 3 | Finite-sample correction invalid for small `n` | Replaced clamp-to-`[1,n]` with **unbounded (±∞) endpoints** when a rank falls outside the sample, or **interval unavailable** ("too early / insufficient calibration") below `minCount` after the all-bins fallback. **Calibration residuals are never clipped** — residual clipping removed from preprocessing; any clipping is display-only. | Data & leakage guards; Core module (finite-sample quantiles); Verification |
| 4 | Inverted CPI baseline | Predicted incremental cost = **`ΔPV(k+h) ÷ CPI`** (CPI = `ΣΔEV/ΣΔAC`), not `ΔPV × CPI`; **fallback to planned-spend `ΔPV` (flagged)** when CPI is zero/undefined/non-finite. | ForecastBaselines; Verification |

## Codex Review — round 3 (2026-07-07)

### Remaining blocking findings
1. **Production/live residual store uses in-sample residuals.** "Single store from the final full fit
   (all eligible rows)" means residuals are computed on rows the model was fit on → understates
   uncertainty (intervals too narrow live). **Fix:** either reserve a fixed, centre-grouped **production
   calibration partition** held out of the serving-model fit, or use **cross-fitted (out-of-fold)
   residuals** collected before refitting the serving model on all data.
2. **Back-test conformal split is not concrete.** "Split by centre-group" states the grouping but not the
   deterministic allocation rule, proportion, seed/hash, or minimum viable fold sizes — and these cannot
   be chosen after seeing results. **Fix:** freeze a **deterministic assignment** (e.g. hash(BccId) mod →
   fixed proper-train/calibration ratio, e.g. 70/30) and define behavior when either partition is below a
   minimum count (skip fold / mark unavailable).
3. **Joint residual simulation needs a complete-case rule.** Drawing joint `(r₁,r₂,r₃)` paths requires all
   three horizon residuals to come from the **same fold model family and the same calibration
   centre-origin key**; per-horizon stores don't define the intersection. **Fix:** build joint paths only
   from calibration centre-origin keys that have **all three** eligible residuals under that fold
   (complete-case); specify fallback/"unavailable" when too few complete paths exist.

### Non-blocking nitpick
- Clarify whether `ΔPV(k+h)` at a historical origin is the schedule as known *then* vs a retrospectively
  revised schedule; if revised, the planned-spend / CPI baselines carry schedule-vintage leakage. State
  the assumption (this project has a single static plan curve, so it's acceptable — but say so).

### Codex round-3 reconciliation

| # | Finding | Resolution | Now in section |
|---|---------|------------|----------------|
| 1 | Live residual store is in-sample | Production store rebuilt as **cross-fitted (out-of-fold) residuals**: centre-grouped `K=10` split, each row's residual scored by a model that did **not** train on it; the serving P50 is refit on all rows for the **point** only, while interval half-widths come from the OOF store — so live intervals reflect genuine out-of-sample uncertainty. (Fixed centre-grouped held-out calibration partition named as the alternative.) | Core module (interval method **b**); Verification |
| 2 | Back-test split not concrete | Frozen **deterministic** allocation: `hash(BccId) mod 10` → buckets 0–6 proper-training (~70%) / 7–9 calibration (~30%), identical across folds & runs, declared before evaluation. Folds below `minTrainRows=40` or `minCalRows=30` are **skipped** (intervals unavailable, excluded from aggregated coverage, drop logged). Added to the frozen-constants list. | Core module (interval method **a**); Data & leakage guards (frozen constants); Verification |
| 3 | Joint sim needs complete-case rule | Joint `(r₁,r₂,r₃)` paths drawn **only from calibration centre-origin keys with all three horizon residuals eligible under the same fold** (complete-case intersection); per-horizon stores expose the shared `(BccId, FeaturePeriodId)`+fold key. Fallback: pooled complete paths → else cumulative cone **unavailable** (trust badge), marginals unaffected. | Core module (joint residual simulation); Verification |
| N | Schedule-vintage nitpick | Stated assumption: single static plan curve (`cost_centre_plan_periods` fixed per published version), so `ΔPV(k+h)` is the plan **as known at origin** → no schedule-vintage leakage; with revised schedules the baseline would need the as-of-origin plan vintage. | Data & leakage guards (schedule-vintage assumption) |

## Codex Review — round 4 (2026-07-07)

### Remaining blocking findings
1. **Residual-store source contradiction.** The frozen/fold-local tier lists the *calibration residual
   store* as a "fold-local fitted statistic computed from the fold's proper-training rows" — which
   contradicts the interval-method construction where residuals come from the **calibration / OOF** rows
   (a model must never score its own training rows for residuals). **Fix:** in the fold-local tier, keep
   only **standardization mean/σ and imputation values** as computed-from-proper-training; state the
   **residual store is populated exclusively from calibration (back-test) / out-of-fold (serving) rows**,
   never from proper-training rows.
2. **Live-serving leakage for historical `k`.** `GET /forecast/cone?period=` + `ForecastCentre(history,k)`
   allow a forecast at an arbitrary historical period `k`, but the serving P50 + OOF residual store are
   fit through the **latest** origin — so a historical `k` forecast uses labels from periods `> k`
   (future leakage). **Fix:** either (a) restrict the live endpoints to the **latest origin only** (the
   genuine "what happens next" forecast; historical origins are evaluated only inside the back-test with
   proper as-of-origin fits), or (b) build an **as-of-`k`** fit + residual store using only rows whose
   target period `< k`. Pick (a) for the product and say so; the back-test (b-style, per-origin) remains
   the credibility artifact.

### Codex round-4 reconciliation

| # | Finding | Resolution | Now in section |
|---|---------|------------|----------------|
| 1 | Residual-store source contradiction | Fold-local **proper-training-fitted** statistics narrowed to **standardization mean/σ + imputation values only**; the **residual store** is split into its own bullet and **populated exclusively from calibration rows (back-test, construct a) / out-of-fold rows (serving, construct b) — never from a model's own proper-training rows**. Verification test-wording updated to match. | Data & leakage guards (two-tier bullet + residual-store bullet); Verification |
| 2 | Live-serving leakage for historical `k` | Adopted **option (a)**: live endpoints serve the **latest origin only**. `/forecast/cone?bcc=` (and cost-centres/rollup) anchor at the centre's latest present period; an arbitrary historical `period=` is dropped/ignored (at most selects the latest anchor). `ForecastCentre(history,k)` fixes live `k` to the latest origin and **refuses a historical `k`** (would leak labels from periods `> k`); historical origins are evaluated **only** in `/forecast/backtest` via proper as-of-origin fits. UI forecasts forward from the latest period (no historical-origin forecasting). As-of-`k` fit noted as option-(b) future work. | Core module (`ForecastCentre`); API (live endpoints); Frontend; Verification |

## Codex Review — round 5 (2026-07-07): CLEAR

**Verdict:** *"No remaining blocking findings. The plan is sound enough to implement without identified
statistical-validity or leakage blockers."* Across five rounds codex raised 16 blocking findings
(round 1: 7, round 2: 4, round 3: 3, round 4: 2) — all folded into the plan body with per-round
reconciliation tables above. The plan is ready to implement.
