# Idea 1 — Early-Warning Classifier: How It Was Actually Built

> Companion to the product-facing [Feature 1 — Early-Warning Watchlist](01-early-warning-watchlist.md)
> and the original spec [`ideas/idea-1-early-warning-classifier.md`](../ideas/idea-1-early-warning-classifier.md).
> This doc is the **engineering reality**: what shipped, where the code lives, and how a QS (or a
> developer) actually uses it.

## From spec to shipped — the short version

The [idea spec](../ideas/idea-1-early-warning-classifier.md) argued for one thing above all: predict
the **GREEN→AMBER transition**, not the AMBER *state* (AMBER is sticky, so "predict next-period AMBER"
is a fake win). It recommended a **transparent gap+CPI rule as the spine**, an optional
gradient-boosted challenger that only earns its place by beating the rule, **rolling-origin
validation**, and honest metrics (precision@5/@10, false alerts per cycle) against **CPI-native
baselines**.

What shipped follows that framing faithfully, with three deliberate departures:

| Spec said | What shipped | Why |
|-----------|--------------|-----|
| Python `score.py` + Streamlit | **C# analytics in `QsEarlyWarning.Core`** + ASP.NET Core 8 API + React SPA | The rest of the product is .NET; one runtime, no Python at build or runtime. |
| Rule spine **+ optional GBT challenger** | **Rule only** (`RuleRiskScore@v1`). CPI-native baselines are computed and reported side-by-side, but **no ML challenger was adopted** | On this workbook the rule already beats the CPI-native baselines; a tree wasn't worth the opacity for the demo. The challenger is documented as a stretch, not built. |
| Single-project notebook | **Multi-tenant, period-dynamic service**: origins derived from each project's own data, served from Postgres under row-level security | Turns a one-off script into a product surface reused by the dashboard, forecast, and copilot. |

Everything else — the transition target, the leakage discipline, the metrics, the honest single-project
caveat — is implemented as specified.

## What the model predicts (unchanged from the spec)

- **Event:** `AlertLevel(p+1) == "AMBER"`, evaluated **only on centres that are GREEN at period `p`**.
  On the Tower X workbook this coincides exactly with next-period `CPI < 0.95` — AMBER is just the
  business-facing label for that CPI crossing (`EvmThresholds.CpiThreshold = 0.95`).
- **Population:** GREEN-and-scoreable rows only. `NOT STARTED`, missing, or non-finite rows are
  **excluded and counted**, never silently dropped (`FeatureBuilder.BuildPairsForPeriod`).
- **Output:** a ranked watchlist — the top `k` (5 or 10) GREEN centres most likely to tip next period,
  each with 2–3 plain-English reason chips.

## The pipeline, end to end

```
Workbook / Postgres → panel (CostCentrePeriod rows)
   │
   ├─ FeatureBuilder            build GREEN→(AMBER?) transition pairs + engineered features
   ├─ RollingOriginEvaluator    walk origins, fit one leakage-safe artifact per origin,
   │                              fit one forecast artifact, compute the validation summary
   ├─ RuleFitter                fit x* and gap_scale on the training prefix ONLY
   ├─ RuleScorer                the frozen RuleRiskScore@v1 formula + reason codes
   └─ WatchlistScoringService   resolve artifact-for-period, rank, return top-k
        │
        ├─ WatchlistController   GET /api/v1/watchlist  (RLS-authorized per request)
        ├─ QsAnalyticsTools      get_watchlist tool exposed to the QS Copilot
        └─ Watchlist.tsx         the "Watchlist" tab in the React SPA
```

All of this lives in `QsEarlyWarning/src/QsEarlyWarning.Core` (analytics),
`.../QsEarlyWarning.Web.API` (endpoints), and `.../frontend/qs-early-warning` (UI). The model is
built once at startup and swapped atomically on reload (`ModelProvider`); scoring **never trains at
request time**.

### 1. Pairing — the transition framing, in code

`FeatureBuilder` groups the panel by cost centre and, for each period `p`, forms a pair only where an
**explicit successor exists at exactly `p+1`** (`Features/FeatureBuilder.cs:43`). Adjacency is never
inferred across a gap. The label is `successor == AMBER`; a GREEN/CLOSED successor is a kept negative;
a NOT STARTED / missing successor is excluded and counted. This is what makes the reported numbers
about *transitions*, not persistence.

### 2. Features (`features@v1`)

Each `TransitionPair` carries: `Cpi`, `Rolling3mCpi`, `Spi`, `VariancePct`, `EacVsBacRatio`, the
**budget/progress gap** = `Pct_Budget_Consumed − Actual_Pct_Complete`, one- and two-period **deltas**
of CPI and gap (exact-predecessor only — `null` across a gap, never differenced blindly), the four
resource-share ratios, and `Discipline`/`PackageCode`. The rule uses `gap` and `Cpi`; the rest power
the reason codes, baselines, and copilot answers.

### 3. The deployed scorer — `RuleRiskScore@v1` (frozen)

From `Scoring/RuleScorer.cs`:

```
RuleRiskScore = 0.7 · clamp01((gap − x*) / gap_scale)  +  0.3 · cpiProximity
cpiProximity  = clamp01(1 − (CPI − 0.95) / 0.10)
```

- `cpiProximity` peaks at the 0.95 line and decays to 0 by `CPI = 1.05`. It is proximity **from
  above** — correct for a GREEN population where CPI ≥ 0.95 (a "distance below 0.95" term would be
  identically zero for every eligible row).
- The weights (`0.7 / 0.3`) and `cpi_band` (`0.10`) are **fixed v1 constants**, asserted, never
  estimated (`Scoring/RuleArtifact.cs`).
- **Only two parameters are fit from data:** `x*` (the gap zero-point) and `gap_scale`. Nothing is a
  black box; the formula is published and versioned.

### 4. Fitting — leakage-safe by construction (`RuleFitter`)

- `gap_scale` = IQR of `gap` over the **training pairs only** (fallback 5pp if degenerate).
- `x*` sweeps the declared grid `{0.0, 0.5, …, 20.0}` pp and maximizes the **macro-mean of
  precision@5** across training cycles; ties break to the smallest `x*`.
- The fit sees **only the `p < cutoff` prefix** — never the fold it will be scored on. Each artifact
  records a SHA-256 fingerprint of the exact training rows for reload validation.

### 5. Rolling-origin training & validation (`RollingOriginEvaluator`)

For each origin `o` from `FirstOrigin`..`LastLabeledPeriod`, the evaluator fits an **out-of-fold (OOF)
artifact** on `p < o` and scores period `o`. This produces one leakage-safe model *per period*, so the
watchlist for period 7 is scored by a model that never saw period 7 or later. A separate
**forecast artifact** is trained on all labeled pairs and scores the latest period (Tower X: 12), which
has no successor yet — this is the "LIVE FORECAST" the QS acts on.

Alongside the rule, three **CPI-native baselines** are scored on every fold: spot `CPI`, one-period CPI
change, and rolling-3M CPI (`Scoring/CpiNativeScorers.cs`). They're reported side-by-side, honestly,
and **never adopted** — there is no adaptive gate.

Origins are **derived from each project's data**, not hard-coded (`Evaluation/ReportingOrigins.cs`):
`ForecastPeriod` = latest present period, `LastLabeledPeriod` = latest period with a present successor,
`FirstOrigin` = first period with ≥3 periods of history before it. For Tower X (periods 1–12) this
yields FirstOrigin=4, LastLabeled=11, Forecast=12 — but a project with periods 1–8 (or a new period 13)
trains and forecasts correctly with no code change.

**Measured result on the Tower X workbook (rolling-origin, 8 folds):**
**precision@5 = 45%** for the rule vs **35%** for the best CPI-native baseline. Reported as per-fold
counts + fold range (no fragile confidence interval) via `GET /api/v1/validation-summary` and the
[Model Validation Panel](06-model-validation-panel.md).

## The math, step by step

Every quantity below is computed in `QsEarlyWarning.Core` / `QsEarlyWarning.Domain`. Nothing is an
opaque model output — you can reproduce each number by hand from the panel columns.

### Inputs (read straight from `9_HISTORICAL_DATA`)

Percentages are stored as **percent (0–100), not fractions**, and the `*_Period` columns are
**cumulative** in this workbook (`AC_AED_Period == AC_AED_Cumulative`). So:

| Symbol | Column | Notes |
|--------|--------|-------|
| `CPI` | `CPI` (= `EV ÷ AC`) | as recorded; ≥ 0.95 for every eligible GREEN row |
| `Pct_Budget_Consumed` | `Pct_Budget_Consumed` | percent, 0–100 |
| `Actual_Pct_Complete` | `Actual_Pct_Complete` | percent, 0–100 |
| `Rolling_3M_CPI` | `Rolling_3M_CPI` | trailing 3-period CPI |
| `AC_{Material,Manpower,Equipment,Subcontract}` | resource split | cumulative basis |

### Step 1 — the gap (the core leading signal)

```
gap = Pct_Budget_Consumed − Actual_Pct_Complete      (percentage points)
```

`CostCentrePeriod.Gap` (`Domain/Entities/CostCentrePeriod.cs:69`). A centre that has consumed 60% of
its budget but is only 45% complete has `gap = +15pp` — spending **15 points ahead of progress**, the
classic tell. `gap` is `null` if either input is missing.

### Step 2 — eligibility (who gets scored)

A row is scored only if it is `IsScoreableGreen` — `AlertLevel == GREEN` **and** `CPI`,
`Pct_Budget_Consumed`, `Actual_Pct_Complete` are all finite (`CostCentrePeriod.cs:62`). Everything else
is excluded (and counted).

### Step 3 — lag deltas (trend, exact-predecessor only)

```
ΔCPI₁(p) = CPI(p) − CPI(p−1)        (null unless period p−1 is present and not NOT STARTED)
Δgap₁(p) = gap(p) − gap(p−1)
ΔCPI₂(p) = CPI(p) − CPI(p−2)
```

`FeatureBuilder.Delta` — never differenced across a missing period (`Features/FeatureBuilder.cs:142`).
`ΔCPI₁ < 0` (CPI falling) feeds a reason chip.

### Step 4 — resource shares

```
share_r = AC_r ÷ AC_cumulative      for r ∈ {Material, Manpower, Equipment, Subcontract}
```

`null` when `AC_cumulative == 0` (`FeatureBuilder.cs:112`). Descriptive only — not in the rule.

### Step 5 — the risk score `RuleRiskScore@v1`

Two normalized components, combined with fixed weights (`Scoring/RuleScorer.cs`):

```
clamp01(z)     = min(1, max(0, z))

gapComponent   = clamp01( (gap − x*) / gap_scale )          # 0 at gap ≤ x*, 1 at gap ≥ x*+gap_scale
cpiProximity   = clamp01( 1 − (CPI − 0.95) / 0.10 )         # 1 at CPI=0.95, 0 at CPI≥1.05

RuleRiskScore  = 0.7 · gapComponent  +  0.3 · cpiProximity   # always in [0, 1]
```

- `x*` and `gap_scale` come from the fitted artifact (Step 6); `0.7`, `0.3`, and the `0.10` CPI band
  are **frozen v1 constants** (`RuleArtifact.cs`).
- `cpiProximity` decays **from above** because every eligible row has `CPI ≥ 0.95` — a "distance below
  0.95" term would be zero for the whole population, which is why it's written as proximity-from-0.95.

**Worked example.** Suppose the fitted artifact for this period has `x* = 3.0pp`, `gap_scale = 8.0pp`.
A centre with `gap = 15pp`, `CPI = 0.97`:

```
gapComponent = clamp01((15 − 3) / 8)      = clamp01(1.5)   = 1.0
cpiProximity = clamp01(1 − (0.97 − 0.95)/0.10) = clamp01(0.8) = 0.8
RuleRiskScore = 0.7·1.0 + 0.3·0.8 = 0.70 + 0.24 = 0.94      → near the top of the watchlist
```

### Step 6 — fitting `x*` and `gap_scale` (training prefix only)

`RuleFitter` (`Scoring/RuleFitter.cs`), never touching the fold it will score:

- **`gap_scale` = inter-quartile range of `gap`** over training pairs:
  `gap_scale = Q3(gap) − Q1(gap)`, with quantiles by linear interpolation at position
  `q·(n−1)`; fallback **5pp** if the IQR is degenerate (< 4 pairs or ≈ 0). Using the IQR makes the
  normalizer robust to outliers and unit-free across projects.
- **`x*` = argmax over the grid `{0.0, 0.5, …, 20.0}` pp** of the training objective:

  ```
  objective(x*) = macro-mean over training feature-periods of  precision@5
  ```

  i.e. for each past period, score its GREEN rows with the probe `x*`, take precision@5, then average
  those per-period values (ties break to the **smallest** `x*`). Only `k=5` drives the fit
  (`EvmThresholds.SelectionK`).

### Step 7 — ranking and the alert set

Within a period, candidates are ordered `(RiskScore desc, BccId asc)` and the **top-k** (5 or 10) is
the alert set (`Metrics.TopK`, `WatchlistScoringService`). Deterministic tie-break, so the same input
always yields the same watchlist.

### Step 8 — the scored metrics (how "45% vs 35%" is computed)

Per fold (one origin period), with `eligible` = scoreable GREEN centres and `positives` = those whose
successor is AMBER (`Metrics.ForFold`):

```
alert set   = top-k by score
TP          = alerted centres whose successor is AMBER
FP          = k_eff − TP
FN          = positives − TP
k_eff       = min(k, eligible)

precision@k = TP / k_eff                 # of the k we told the QS to chase, how many really tipped
recall      = TP / positives             # null when a fold has zero AMBER transitions (excluded)
```

The **headline number is the macro-mean of precision@k across folds** — average the per-fold precisions
(skipping nulls), rather than pooling, so no single busy period dominates (`Metrics.Macro`). Running
this for the rule and for each CPI-native baseline over the 8 Tower X folds gives
**rule precision@5 = 45%** vs **best baseline = 35%**. Recall, raw TP/FP/FN counts, and the fold range
are reported alongside — never accuracy, which a "GREEN-forever" predictor would ace while catching
zero transitions.

> **Why precision-first:** the QS acts on the top of the list, so the cost that matters is *false
> alerts per cycle* (wasted chases). A watchlist that cries wolf gets ignored — hence the fit
> optimizes precision@5 and the report leads with it.

## Validating the results

The numbers above (117 transitions, precision@5 = 45% vs 35%, the leakage discipline) are not
asserted on faith — they are pinned by an automated test suite and reproducible from the live API.
There are four independent ways to check them.

### 1. Run the test suite — `dotnet test`

~88 test methods across `tests/QsEarlyWarning.Tests`. The ones that validate *this* feature, and
exactly what each proves:

**Data contract** (`DataContractTests.cs`) — the ground truth every downstream number rests on:

| Assertion | What it locks down |
|-----------|--------------------|
| `Panel.Count == 2076`, `173` distinct centres | the panel is loaded whole (173 centres × 12 periods) |
| `1163` live GREEN/AMBER rows | the eligible universe |
| **`0` rows where `(AlertLevel==AMBER) != (CPI<0.95)`** | proves AMBER ≡ CPI<0.95 — so "predict AMBER" really is "forecast the CPI crossing" |
| `AlertLevel ∈ {GREEN, AMBER, CLOSED, NOT STARTED, null}` | no surprise statuses leak into pairing |

**Transition framing** (`TransitionPairTests.cs`) — proves the target is the flip, not the state:

- `Real_workbook_has_117_green_to_amber_transitions` — **117** positive labels over a **670**-row
  paired population (117 AMBER + 3 CLOSED + 550 GREEN successors).
- `Green_to_not_started_is_excluded_and_counted` and `No_false_adjacency_across_a_missing_period` —
  a `p→p+2` jump is **never** treated as adjacent; gaps are excluded and counted, not bridged.
- `Lag_delta_requires_exact_predecessor_else_missing` — `ΔCPI₁` is `null` across a missing period,
  never differenced blindly.

**Leakage / lifecycle** (`ArtifactLifecycleAndEvaluationTests.cs`) — the anti-cheating guarantees:

- `Oof_artifacts_exist_for_origins_4_through_11` + `No_artifact_before_min_origin` — one out-of-fold
  model per period, each trained strictly on `p < o`; no model exists for a period with too little
  history.
- `Forecast_period_12_scores_its_green_population_despite_no_successor` — the live forecast ranks all
  **113** GREEN centres at period 12 in descending risk.
- `Scoring_is_deterministic_for_same_input_and_artifact` — identical input ⇒ identical watchlist.
- `Evaluation_produces_eight_folds_...` — **8 folds**, **117** transitions, and a hard assertion that
  **the deployed rule ≥ the best CPI-native baseline** at precision@5. This is the test that fails if
  the rule ever stops beating the honest baselines.

**Metric definitions** (`MetricsTests.cs`) — `precision@k = TP / min(k, eligible)`, deterministic
`(score desc, BccId asc)` tie-break, and zero-positive folds yielding `null` recall (excluded from the
macro-mean, never counted as 0).

> Note: `QsEarlyWarning.Db.Tests` needs a Postgres instance and `CopilotLiveEvalTests` needs an
> Anthropic API key — skip those two to validate the classifier offline; the tests above need only the
> workbook.

### 2. Read the live backtest — `GET /api/v1/validation-summary`

The model-level, frozen out-of-fold report the [Model Validation Panel](06-model-validation-panel.md)
renders. It returns, for the rule **and** each CPI-native baseline, at k=5 and k=10: macro precision,
macro recall, the **per-fold precision range** `[min, max]`, false alerts per cycle, and the raw
per-fold `Eligible / Positives / TP / FP / FN` counts. Because it exposes every fold, you can
recompute the macro-mean yourself and confirm the headline. Its `Provenance` string states plainly
that this is exploratory single-project evidence — no cross-project claim.

### 3. Reproduce a single row by hand

Pick any centre on the watchlist and recompute its score from panel columns:

1. `gap = Pct_Budget_Consumed − Actual_Pct_Complete` (percentage points).
2. Read `x*` and `gap_scale` for that period from the artifact (or the validation summary), then apply
   the Step-5 formula: `0.7·clamp01((gap−x*)/gap_scale) + 0.3·clamp01(1−(CPI−0.95)/0.10)`.
3. It must equal the `riskScore` the API returned for that row, and the ranking must match
   `(score desc, BccId asc)`.

If they don't tie out, suspect the join/period alignment before the model — the scoring path is a pure
function of `gap` and `CPI`.

### 4. Sanity-check against the honest baselines and the trivial one

The result is only meaningful *relative to what it beats*:

- **Beat the CPI-native baselines** (spot CPI, ΔCPI₁, rolling-3M CPI) — enforced by the fold test in
  §1 and visible side-by-side in §2. If the rule can't clear them, it shouldn't ship.
- **Ignore the trivial "already-AMBER" baseline** — it catches **zero** transitions by construction
  (AMBER rows are excluded from scoring), so beating it proves nothing; it exists only to show why the
  transition framing is the real task.
- **Judge on precision@k and recall of the transition, never accuracy** — a "GREEN-forever" predictor
  scores >90% accuracy and is useless; that's why accuracy is not reported.

### What validation does *not* claim

- **Single project.** All of the above is the Tower X workbook. The 45%/35% is Tower X's, not a
  universal benchmark — no cross-project generalization is asserted.
- **Thin positives.** 117 transitions across 74 centres over 12 periods; one unlucky fold can move the
  macro-mean, which is why per-fold counts and the `[min, max]` range are reported, not just the average.
- **A CPI-crossing forecaster.** Since AMBER ≡ CPI<0.95 here, "validated" means the rule forecasts the
  next-period CPI crossing well — honestly labeled, not dressed up as hidden-signal discovery.

## How users make use of it

There are three entry points, all reading the **same scoring path** so there's no drift between what
was validated and what's shown.

### A. The Watchlist tab (primary — for the QS)

Open the app → **Watchlist** tab. Pick a **period** and **k (5 or 10)**. You get a ranked table:
rank, cost centre, discipline, a **risk bar**, current **CPI**, the **budget/progress gap (pp)**, and
**"Why flagged"** chips (e.g. *"spending 18.0pp ahead of progress"*, *"CPI 0.962 — close to the 0.95
line"*, *"CPI down 0.031 since last period"*).

A badge tells you which model you're looking at:
- **LIVE FORECAST** — the latest period, the one to act on now.
- **HISTORICAL (out-of-fold)** — an earlier period scored by a model trained strictly on its past,
  for reviewing how the watchlist would have called it.

Clicking a row hands off to the [Variance Attribution Bridge](05-variance-attribution-bridge.md) to
explain *which resource* the drift is attributed to.

**Workflow:** once per reporting cycle, open the LIVE FORECAST watchlist, chase the top 5 while the
money is still unspent, and use the chips to know *why* each centre is listed.

### B. The API (for integration / scripting)

```
GET /api/v1/watchlist?period={p}&k={5|10}
Headers: X-User-Id, X-Project-Slug        # authenticated identity + selected project
```

Response: `{ period, k, isForecast, artifactVersion, trainingCutoffPeriod, eligibleCount, rows[] }`,
each row `{ rank, bccId, discipline, packageCode, riskScore, cpi, gap, riskIndicators[] }`.

Every request is **authorized against row-level security** before any project data is read — a cache
hit never bypasses the membership check. Errors: `401` no identity · `403` not a member · `404`
unknown project / valid period with no artifact · `400` malformed `period`/`k`. The valid `period`
range is derived per project (Tower X: 4–12).

### C. The QS Copilot (plain English)

The scorer is wired into the copilot as the **`get_watchlist`** tool
(`Core/Agent/QsAnalyticsTools.cs`). Ask *"which centres are about to go amber?"* or *"why is BCC-… on
the watchlist?"* and the copilot calls the exact same ranking the tab shows — no separate, drifting
logic. See [QS Copilot](04-qs-copilot.md).

## Honest limits

- **Single-project evidence.** Validated on the Tower X workbook only. No cross-project generalization
  is claimed — the 45% vs 35% number is Tower X's, not a universal benchmark.
- **Thin positives.** 117 GREEN→AMBER transitions across 74 centres over 12 periods; a single
  lucky/unlucky fold can move the rates, which is why per-fold counts (not just averages) are reported.
- **It forecasts a CPI crossing.** Because AMBER ≡ `CPI < 0.95` on this data, the model is honestly a
  next-period CPI-threshold forecaster with a business-facing label — not hidden-signal discovery.
- **No ML challenger shipped.** The gradient-boosted alternative remains a documented stretch; the
  frozen rule is the deployed scorer.

## Where to look in the code

| Concern | File |
|--------|------|
| Transition pairing + features | `Core/Features/FeatureBuilder.cs`, `Core/Features/TransitionPair.cs` |
| Frozen scorer + reason codes | `Core/Scoring/RuleScorer.cs`, `Core/Scoring/RuleArtifact.cs` |
| Parameter fitting (`x*`, `gap_scale`) | `Core/Scoring/RuleFitter.cs` |
| Rolling-origin train + validation | `Core/Evaluation/RollingOriginEvaluator.cs`, `Core/Evaluation/ReportingOrigins.cs` |
| CPI-native baselines | `Core/Scoring/CpiNativeScorers.cs` |
| Serving path (rank top-k) | `Core/Scoring/WatchlistScoringService.cs` |
| HTTP endpoint (RLS-authorized) | `Web.API/Controllers/WatchlistController.cs` |
| Copilot tool | `Core/Agent/QsAnalyticsTools.cs` |
| UI | `frontend/qs-early-warning/src/components/Watchlist.tsx` |
| Frozen constants (0.95, top-k, seed) | `Domain/Constants/EvmThresholds.cs` |
