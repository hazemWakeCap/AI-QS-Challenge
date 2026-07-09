# Idea 2 — Incremental-Spend Forecaster: How It Was Actually Built

> Companion to the product-facing [Feature 2 — Cost-Trajectory Forecaster](02-cost-trajectory-forecaster.md)
> and the original spec [`ideas/idea-2-eac-forecaster.md`](../ideas/idea-2-eac-forecaster.md).
> This doc is the **engineering reality**: what shipped, where the code lives, the math, and how a QS
> (or a developer) actually uses it. It mirrors the deep dive for
> [Idea 1](12-idea-1-implementation.md).

## From spec to shipped — the short version

The [idea spec](../ideas/idea-2-eac-forecaster.md) was itself the product of a CEO-mode reframe: the
Tower X workbook has **no final-cost ground truth** (median last-period progress ~13%, only 4 of 173
centres ever finish, and `EAC_AED` is *literally* `BAC ÷ CPI`), so a "validated final-cost EAC" is
unfalsifiable here. The spec therefore demanded a pivot to the claim the data *can* support: a
calibrated **short-horizon incremental-spend** forecast (h = 1, 2, 3 evaluated separately), scored
against realized per-period spend, with a **split-conformal** P10–P90 band, a **grouped rolling-origin**
back-test against **four baselines**, and the final-cost cone kept only as a subordinated *directional*
overlay.

What shipped follows that framing faithfully, with three deliberate departures:

| Spec said | What shipped | Why |
|-----------|--------------|-----|
| Python `forecast.py` + Streamlit app + a back-test notebook | **C# analytics in `QsEarlyWarning.Core.Forecasting`** + ASP.NET Core 8 API + React SPA; the back-test is a live endpoint + an xUnit test, not a notebook | The rest of the product is .NET; one runtime, no Python at build or run time. The "credibility artifact" is `GET /api/v1/forecast/backtest` and `ForecastTests`, reproducible on every build. |
| "Predict in **cost space**, never by dividing by CPI" | **Model in BAC-*fraction* space** (label and features are `ΔAC ÷ BAC`), then multiply the prediction by `BAC` to get cost | Same guarantee — it never divides by CPI — but one scale-free model serves centres whose budgets span orders of magnitude. Still a cost-space forecast. |
| Point model left open; learned quantile ensemble **cut** | **Hand-rolled ridge regression** for the P50 point; the band is **split-conformal residual quantiles** (no learned quantile model) | Ridge is transparent, dependency-free, and closed-form. The conformal band is calibrated *by construction* on the sample size we actually have — the learned ensemble can't be calibrated on 4 completers, exactly as the spec argued. |

Everything else — the incremental-spend target, the leakage discipline, the four baselines, the
MAE-%BAC / WAPE metrics, the measured (not asserted) coverage, the Monte-Carlo project roll-up, and the
directional-only final-cost cone — is implemented as specified.

## What the model predicts

- **Target (validated):** the **realized single-period spend increment** `ΔAC(k+h) = AC_cumulative(k+h)
  − AC_cumulative(k+h−1)`, at horizons **h = 1, 2, 3 separately**. Never cumulative AC, never `EAC_AED`
  — those would be circular (`EAC_AED == BAC/CPI` exactly on this data).
- **Modelled quantity:** that increment as a **fraction of BAC** (`ΔAC ÷ BAC`), converted back to cost
  by `× BAC` at serving time.
- **Serving anchor:** live forecasts are produced **only at the latest origin** (Tower X: period 12).
  Earlier origins are used purely in the back-test, so the live number is never contaminated by future
  data.
- **Output per centre:** three horizon increments each as a **nominal-80% interval** (P10 / P50 / P90),
  a **trust badge**, a **cumulative cost cone** over BAC/AC, and a subordinated **directional final
  cost**.

## The pipeline, end to end

```
Workbook / Postgres → panel (CostCentrePeriod rows)   [built once per tenant snapshot]
   │
   ├─ ForecastFeatureBuilder      build per-centre × per-horizon increment samples (BAC-fraction space)
   ├─ RidgeRegressor              closed-form standardized ridge (the P50 point model)
   ├─ IncrementalSpendForecaster  fit per-horizon ridge + cross-fitted OOF residual store; serve @ latest origin
   │      ├─ HorizonBand          P50 + split-conformal P10/P90 per horizon
   │      ├─ CumulativeCone       complete-case JOINT residual-path simulation (not endpoint summing)
   │      └─ Rollup               Monte-Carlo project spend scenario across centres
   ├─ ForecastEvaluator           grouped rolling-origin back-test: model + 4 baselines on identical rows
   └─ ForecastBaselines           zero-increment · planned-spend · recent-run-rate · cpi-based
        │
        ├─ ForecastController     GET /api/v1/forecast/{cost-centres,cone,rollup,backtest}  (RLS-authorized)
        ├─ QsAnalyticsTools       forecast_incremental_spend + directional_eac tools for the QS Copilot
        └─ ForecastCone.tsx /     the "Forecast" tab in the React SPA
           ForecastBacktest.tsx
```

All analytics live in `QsEarlyWarning/src/QsEarlyWarning.Core/Forecasting/`; the endpoints in
`.../QsEarlyWarning.Web.API`; the UI in `.../frontend/qs-early-warning`. The forecaster is **fit once
when the project snapshot is built** (`ProjectSnapshotRegistry`) and never trains at request time; a
fit failure degrades gracefully to `null` (the watchlist and EVM still work).

## The math, step by step

Every quantity is computed in `QsEarlyWarning.Core.Forecasting`. Nothing is an opaque model output.

### Step 1 — the increment (the leakage-safe target and the core features)

`IncrementHelper` forms every difference **only across an exactly-adjacent present predecessor** — never
across a gap:

```
ΔAC(k)  = AC_cumulative(k) − AC_cumulative(k−1)      # actual-cost increment  (the label engine)
ΔPV(k)  = PV(k) − PV(k−1)                            # planned-value increment
ΔEV(k)  = EV(k) − EV(k−1)                            # earned-value increment
```

`rollCpi(k) = ΣΔEV ÷ ΣΔAC` over the ≤3 present periods ending at `k` (sum-of-increments, **not** a mean
of per-period ratios — that is what stops the CPI-to-102 blow-up). `runRate(k)` = mean of the ≤3 present
`ΔAC` increments. Any of these is `null` if its predecessor period is missing (`IncrementHelper.cs`).

### Step 2 — the features (`ForecastFeatureBuilder`)

Six features, all computed strictly from periods **≤ k** (the feature period) and expressed as fractions
of BAC or as ratios:

| # | Feature | Definition | Notes |
|---|---------|-----------|-------|
| 0 | `plannedTargetInc` | `ΔPV(k+h) ÷ BAC` | The plan curve is **static** (known as-of origin), so the *target-period* planned increment carries no schedule-vintage leakage on this project. |
| 1 | `recentInc` | `ΔAC(k) ÷ BAC` | last observed spend increment |
| 2 | `prevInc` | `ΔAC(k−1) ÷ BAC` | the one before |
| 3 | `rollCpi` | `ΣΔEV ÷ ΣΔAC` over ≤3 periods | robust rolling CPI |
| 4 | `progressFrac` | `Actual_Pct_Complete ÷ 100` | |
| 5 | `runRate` | mean `ΔAC` over ≤3 periods `÷ BAC` | also the fallback prediction |

The **design row** is these 6 features concatenated with 6 **was-missing indicators** (0/1) —
`ForecastFeatureBuilder.Design`, 12 columns. A NaN feature is ridge-imputed to the training mean *and*
flagged, so "this input was absent" is itself a signal rather than a silent zero.

**Leakage guard (enforced):** `EAC_AED`, `VAC_AED`, and `EAC_vs_BAC_Ratio` are **never** features —
`EAC_AED` is exactly `BAC/CPI`, so it would leak the very baseline the model is benchmarked against.

### Step 3 — the P50 point model (`RidgeRegressor`)

Closed-form ridge, hand-rolled (no ML dependency):

- Features are **standardized on the training rows only** (mean/σ stored, reapplied at predict).
- The **intercept is unpenalized**; the penalty `A = ZᵀZ + λ·diag(0,1,…,1)`, `b = Zᵀy`.
- Solved by **Cholesky** of the SPD normal matrix. On a rank-deficient/non-SPD pivot it retries with a
  higher `λ` (ridge floor **1 → 10 → 100**); if all fail, that horizon **falls back to the run-rate
  feature** rather than emitting garbage (`IncrementalSpendForecaster.TryFit`).
- `λ = 1.0`, frozen (`ForecastConfig.Lambda`).

The prediction is a **fraction of BAC**; serving multiplies by the centre's `BAC` to get a cost.

### Step 4 — the split-conformal band (P10 / P90)

The interval is **not** a learned quantile — it is the empirical quantiles of held-out **residuals**,
binned by progress:

```
resid = actualFraction − ridge.predictFraction        # on rows the ridge did NOT train on
bins  = progress % ∈ [0,10,25,50,100]                 # ForecastConfig.ProgressBinEdges
```

For a target row, take that bin's residuals (**pooled fallback** across all bins if the bin has
`< MinCount = 60`; **unavailable** if even the pool is `< 60`), then read the split-conformal quantiles:

```
α = 0.20  →  nominal 80% interval
lowerRank = floor((n+1)·α/2)      upperRank = ceil((n+1)·(1 − α/2))
P10 = (predFraction + resid[lowerRank]) × BAC         # null rank → −∞ (unbounded below)
P90 = (predFraction + resid[upperRank]) × BAC         # null rank → +∞ (unbounded above)
```

(`IncrementalSpendForecaster.ConformalResidQuantiles`.) Because the band is built from real held-out
errors, **coverage is a measured property, not an assumption** — see Step 7.

There are **two residual regimes**, deliberately different:

- **Serving residuals** are **cross-fitted out-of-fold**: 10 folds by a stable `hash(BccId) mod 10`
  (`ForecastConfig.Fold`, a seedless FNV-1a hash — identical across runs), each row scored by a ridge
  that did *not* train on its fold. This is the honest residual store for the live band.
- **Back-test residuals** use the frozen **centre-group split** (`hash mod 10`: buckets 0–6 =
  proper-training, 7–9 = calibration), so the interval evaluated at each origin is calibrated on
  strictly earlier, disjoint centres.

### Step 5 — the trust badge

```
progress < 10%                      → TooEarly                (ForecastConfig.ProgressGatePct)
else h=1 band unavailable           → InsufficientCalibration
else                                → Validatable
```

The badge is the honest "is this number trustworthy yet?" signal the spec demanded — a centre 13% done
is flagged, not dressed up.

### Step 6 — the cumulative cost cone (joint simulation, not summed quantiles)

The cone over BAC/AC is reconstructed by **complete-case joint residual-path simulation**
(`CumulativeCone`). It takes calibration rows keyed `(BccId, featurePeriod, fold)` that are present in
**all three** horizon stores, and for each such path adds the *same centre's* h1/h2/h3 residuals to the
P50 increments:

```
cum(h) = AC_at_origin + Σ_{j≤h} (P50[j] + resid_j × BAC)      # per shared path
P10/P50/P90(period o+h) = empirical quantiles across paths
```

This **preserves the correlation between successive horizons within a centre** — summing three
independent P90s (which assumes perfect correlation) would overstate the band. If joint calibration is
insufficient, the cone is marked unavailable and only the P50 trajectory is drawn.

### Step 7 — the metrics (`ForecastMetrics`)

Per horizon, on identical eligible rows:

```
MAE-%BAC  = mean OVER CENTRES of the centre's mean(|actual − pred| ÷ BAC)   # centres weighted equally
WAPE      = global Σ|actual − pred| ÷ Σ|actual|
coverage  = fraction of actuals inside [P10,P90]  (reported with n + a 95% Wilson band)
```

MAE-%BAC and WAPE are **both** reported (they answer different questions). Coverage is **measured and
reported, never asserted** — the UI states plainly that an achieved fraction below the nominal 80%
reflects temporal drift, because the calibration set is strictly earlier than the evaluated period.

## The back-test — the credibility artifact (`ForecastEvaluator`)

Grouped **rolling-origin**. For each evaluation origin `o` from `FirstOrigin`..`ForecastPeriod`:

1. **Training pool** = every sample whose **target period `< o`** — the leakage guard is on the *label*
   period, not the feature period, so no residual the model is scored on ever entered its training.
2. Split by the frozen centre-group rule into **proper-training** (fits the fold's ridge) and
   **calibration** (yields the per-bin residuals and the interval). A centre is always on one side.
3. **Evaluate** the rows realized exactly at `o` — the **model and all four baselines on the identical
   eligible rows**.
4. Folds with `< 40` proper-training or `< 30` calibration rows, or where the ridge won't fit, are
   **skipped and counted** (`FoldsSkipped`), never silently dropped.

Results are summarized **overall** and for the **early band** (`progress < 40%`, `ClaimBandPct`) — the
noisy region this idea targets and where the win is claimed. The `Provenance` string is explicit that
centres recur across folds (temporal out-of-fold, **not** new-centre generalization).

**The four baselines** (`ForecastBaselines`), each on the same rows:

| Baseline | Prediction |
|----------|-----------|
| `zero-increment` | `0` (random-walk on cumulative AC) |
| `planned-spend` | `ΔPV(k+h)` |
| `recent-run-rate` | last run-rate (`runRate`, fallback `recentInc`) |
| `cpi-based` | `ΔPV(k+h) ÷ CPI` — **divide** the planned value-to-do by efficiency (not `BAC/CPI`); falls back to planned-spend, flagged, when `CPI ≤ 0` |

## Validating the results

The numbers are pinned by an automated suite and reproducible live. Three ways to check them:

### 1. `dotnet test` — `ForecastTests` (4 tests, all passing)

`tests/QsEarlyWarning.Tests/ForecastTests.cs`:

| Test | What it locks down |
|------|--------------------|
| `Increment_is_the_consecutive_cumulative_difference` | the target is `AC(k)−AC(k−1)`, exact-predecessor — not cumulative AC |
| `Backtest_scores_model_and_four_baselines_on_identical_rows_with_measured_coverage` | **5 predictors** (model + 4 baselines) per horizon, **identical `N`**, and a **non-null measured coverage** |
| `Model_beats_all_four_baselines_on_mae_pct_bac_in_the_early_band` | on this panel the model's MAE-%BAC ≤ **every** baseline at h=1 in the `<40%` band — the test that fails if the model ever stops earning its place |
| `ForecastCentre_yields_three_horizons_anchored_at_the_latest_origin` | serving returns exactly 3 horizons, anchored at the latest origin |

> The back-test **reports** the comparison — a legitimate outcome could show no win. On the Tower X
> workbook the model does beat all four baselines in the early band, so the win is asserted here. This
> is single-project evidence, not a universal benchmark.

### 2. Read the live back-test — `GET /api/v1/forecast/backtest`

Returns, for the model **and** each baseline, at h=1,2,3: MAE-%BAC, WAPE, measured coverage with its
Wilson 95% band and `n`, the folds evaluated/skipped, the origin range, and the frozen-config notes —
for both the overall and early bands. You can recompute the headline yourself from the fold rows.

### 3. Reproduce a single row by hand

The serving path is a pure function: `gap`-free here, it is `predFraction = ridge(designRow)`,
`P50 = predFraction × BAC`, and `P10/P90 = (predFraction + residualQuantile) × BAC`. If a row doesn't
tie out, suspect the join/period alignment before the model.

### What validation does *not* claim

- **Single project.** All of it is the Tower X workbook. No cross-project generalization is asserted;
  centres recur across folds (temporal OOF, not new-centre).
- **No validated final cost.** Median ~13% progress, 4 completers. The final-cost cone is **directional
  only** and labelled as such everywhere it appears.
- **Coverage below nominal.** The 80% is a *nominal* interval; the achieved fraction is measured and
  reported (with a Wilson band), and is expected to sit below 80% under temporal drift.

## How users make use of it

Three entry points, all reading the **same forecaster** built into the project snapshot — no drift
between what was validated and what's shown.

### A. The Forecast tab (primary — for the QS)

Open the app → **Forecast** tab (`ForecastCone.tsx` + `ForecastBacktest.tsx`):

- **Cost cone.** Pick a cost centre. You get the **P50 trajectory with a shaded P10–P90 band** over the
  BAC reference and AC-to-date, a **trust badge** (Validatable / Too Early / Insufficient Calibration),
  and a per-horizon table of P10/P50/P90 increments. If joint calibration is thin, the band is hidden
  and only the P50 line shows — honestly stated.
- **Project spend scenario.** A one-line Monte-Carlo (2,000 draws) roll-up of next-period (h=1) spend
  across centres: P10 · P50 · P90 — labelled a **scenario spread, not a probability** (it assumes centre
  independence, so it understates common shocks).
- **Directional final cost.** Hidden behind a checkbox, labelled "not validated — BAC/CPI-style
  extrapolation; no final-cost ground truth on this project."
- **Back-test panel.** The MAE-%BAC table (model vs 4 baselines, best cell highlighted, early/overall
  toggle) and the measured-coverage table with Wilson bands.

**Workflow:** each reporting cycle, read the next-period band for cash-flow/commitment planning; trust
it where the badge says Validatable; treat the final-cost cone as directional only.

### B. The API (for integration / scripting)

```
GET /api/v1/forecast/cost-centres          # per-centre horizon-1 forecast + trust badge (list)
GET /api/v1/forecast/cone?bcc={id}         # one centre: h1-3 increments + cumulative cone + directional EAC
GET /api/v1/forecast/rollup                # Monte-Carlo project next-period spend scenario
GET /api/v1/forecast/backtest              # the full grouped rolling-origin comparison
Headers: X-User-Id, X-Project-Slug         # authenticated identity + selected project
```

Every request is **authorized against project membership** before any data is read
(`ForecastController.Resolve`). Errors: `401` no identity · `403` not a member · `404` no data /
unknown centre · `503` forecast unavailable (couldn't fit for this project). The live origin and the
valid horizons are derived per project, not hard-coded.

### C. The QS Copilot (plain English)

The forecaster is wired into the copilot as **two deliberately separate tools**
(`Core/Agent/QsAnalyticsTools.cs`):

- **`forecast_incremental_spend`** — the **validated** next-period forecast (h=1,2,3 with P10/P50/P90 and
  the trust badge). It **deliberately does not return a final-cost number** — `DirectionalFinalCost` is
  omitted from the projection so the copilot can't accidentally present it as validated.
- **`directional_eac`** — the workbook `EAC = BAC/CPI` (and `VAC`), returned **flagged
  `validated = false`** with a note steering the user back to `forecast_incremental_spend` for a number
  to trust.

Ask *"what's the spend forecast for BCC-…?"* and the copilot calls the exact same ranking the tab shows.

## Honest limits

- **Single-project evidence.** Validated on the Tower X workbook only; centres recur across folds
  (temporal OOF, not new-centre generalization).
- **Final cost is directional.** No ground truth on this project; the cone and `directional_eac` are
  labelled not-validated everywhere.
- **Nominal 80% band.** Coverage is measured and reported with a Wilson interval, expected below 80%
  under drift — never asserted.
- **Roll-up is a scenario.** Monte-Carlo across centres assuming independence — a spread, not a
  calibrated probability of over/under-budget.
- **Learned quantile ensemble not shipped.** Deferred by design (can't calibrate on 4 completers); the
  transparent ridge + split-conformal band is the deployed forecaster.

## Where to look in the code

| Concern | File |
|---------|------|
| Frozen config, sample/DTO records, trust badge | `Core/Forecasting/ForecastModels.cs` |
| Feature engineering (6 features + missing flags) | `Core/Forecasting/ForecastFeatureBuilder.cs` |
| Exact-predecessor increment / rolling-CPI helper | `Core/Forecasting/IncrementHelper.cs` |
| Closed-form ridge (P50 point model) | `Core/Forecasting/RidgeRegressor.cs` |
| Fit + serve + conformal band + cone + roll-up | `Core/Forecasting/IncrementalSpendForecaster.cs` |
| Grouped rolling-origin back-test | `Core/Forecasting/ForecastEvaluator.cs` |
| The four baselines | `Core/Forecasting/ForecastBaselines.cs` |
| MAE-%BAC / WAPE / coverage / Wilson | `Core/Forecasting/ForecastMetrics.cs` |
| Snapshot wiring (fit once, degrade gracefully) | `Core/Registry/ProjectSnapshotRegistry.cs` |
| HTTP endpoints (RLS-authorized) | `Web.API/Controllers/ForecastController.cs`, `Web.API/Contracts/ForecastDtos.cs` |
| Copilot tools (validated vs directional) | `Core/Agent/QsAnalyticsTools.cs` |
| UI | `frontend/qs-early-warning/src/components/ForecastCone.tsx`, `.../ForecastBacktest.tsx` |
| Tests | `tests/QsEarlyWarning.Tests/ForecastTests.cs` |
</content>
</invoke>
