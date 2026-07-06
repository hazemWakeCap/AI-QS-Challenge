# Implementation Plan — Idea 1: Early-Warning Drift Classifier (ASP.NET + React + Microsoft Agent Framework)

> Source spec: [`ideas/idea-1-early-warning-classifier.md`](../ideas/idea-1-early-warning-classifier.md).
> This revision re-homes the idea onto the **WakeCap production stack** — a layered ASP.NET Core 8
> solution + React micro-frontend + a **Microsoft Agent Framework** copilot sitting on top — instead of the
> earlier Python/Streamlit prototype. The analytics (EVM derivation, drift scoring, rolling-origin
> back-test) are **reimplemented in C#**; there is no Python at build or runtime.
>
> Conventions mirror `~/Wakecap/Backend/wakecap-app-api` (net8.0, `.Web.API`/`.Core`/`.Domain`/
> `.Infrastructure`/`.SharedKernal`/`.Tests`) and `~/Wakecap/Frontend/frontend-2.0` (React 18 + TS +
> `@tanstack/react-query` + `@wakecap/frontend-components`). The copilot clones the proven
> `ClaudeTrainingCenterAgent` pattern (read-only analytics tools over `Microsoft.Agents.AI` +
> `Microsoft.Agents.AI.Anthropic`, behind a swappable interface).

## 1. Goal

Ship a **QS Cost Early-Warning system** with three surfaces over one shared scoring core:

1. **API + React watchlist** — a ranked list that flags a GREEN cost centre **one reporting period before**
   it tips to AMBER, so the QS acts while money is still unspent.
2. **QS Cost Copilot** — a Microsoft Agent Framework agent (Claude-backed) that answers natural-language
   questions ("which centres are about to drift and why?") by calling read-only tools over the same scoring
   core. No new model config; reuses the WakeCap agent pattern.

**The modeled target is `AlertLevel(p+1) == "AMBER"`** — the label, not a raw CPI inequality. On live
GREEN/AMBER rows this coincides with "next period's `CPI` breaches 0.95" (`AMBER ≡ CPI < 0.95`, verified on
all 1,163 such rows), which is why we describe it as catching CPI drift early. But the label is the target
of record because other successor states exist (`CLOSED`, `NOT STARTED`) whose CPI does **not** define the
event: a `GREEN→CLOSED` successor is a **non-AMBER outcome = negative**, and a `GREEN→NOT STARTED` successor
is **excluded** (§6.3). Using the AMBER label (not `CPI(p+1)<0.95`) keeps those cases unambiguous.

Success = on GREEN→AMBER transitions, we **report** how a transparent C# rule (and an optional, descriptive
ML.NET challenger) compares to CPI-native baselines on **precision@k** and **false-alerts-per-cycle**,
validated with rolling-origin folds reported as **per-fold counts + fold range** (no bootstrap CI — see
§6.6). The **transparent rule is the predeclared, deployed scorer**; the challenger is shown side by side as
exploratory evidence and is **never adopted from the eval folds** — there is no adaptive selection gate, so
the backtest is unbiased for the one deployed scorer.

**Statistical guardrails (these bound every claim):** 117 transitions come from **74 centres over 11
possible period-steps** — rows are **not IID** (serial correlation + repeated centres), so metrics are
reported per fold, never as if pooled rows were independent. With this few events a 5-point precision
difference can be a single flipped prediction; that is why nothing is selected on the folds and the
challenger is descriptive only. This is an *exploratory, single-project* model on an
organiser-generated/reconciled workbook — results may partly reflect generation formulas, not real site
behaviour; **no cross-project generalisation claim**.

## 2. Tech stack

| Layer | Choice | Why |
|-------|--------|-----|
| Runtime | **.NET 8 (`net8.0`)** | Matches every WakeCap backend csproj; LTS. |
| Solution shape | Layered: `.Web.API` → `.Core` → `.Domain` / `.Infrastructure` / `.SharedKernal` / `.Tests` | Exact WakeCap layering (`wakecap-app-api`). |
| Excel read | **ClosedXML** (MIT) | Reads the row-5-header `9_HISTORICAL_DATA` sheet without Python; no license constraint (unlike EPPlus commercial). |
| Data frame math | Plain C# (LINQ over typed records) | EVM identities + trend deltas are simple per-`BCC_ID` scans; no pandas needed. |
| Baseline rules | Plain C# | CPI-native + gap thresholds are 1–2 variable rules. |
| Challenger model | **ML.NET `FastTree` binary classifier** (`Microsoft.ML` + `Microsoft.ML.FastTree`) | .NET-native gradient-boosted trees — the C# equivalent of the earlier HistGBDT; keeps the stack single-language. |
| Evaluation | Custom rolling-origin harness in `.Core` | No off-the-shelf walk-forward for this transition target. |
| Agent | **`Microsoft.Agents.AI` 1.3.0 + `Microsoft.Agents.AI.Anthropic` `1.3.0-preview.260423.1`** (Claude via `IChatClient`) | Pin the **exact prerelease** Anthropic version from `Wakecap.App.SmartImport.csproj` (it is not a stable `1.3.0`); compile against that API. Read-only tool surface. |
| API | **ASP.NET Core Web API** (controllers, Asp.Versioning) | Same as `Wakceap.App.Web.API`. |
| Frontend | **React 18 + TypeScript 5 + `@tanstack/react-query` 4 + `@wakecap/frontend-components`** | Same as `frontend-2.0` packages; single sortable watchlist + copilot chat panel. |
| Tests | **xUnit** | Same as `Wakecap.App.Tests`. Leakage guards + data-contract + transition-pair correctness. |
| Observability | OpenTelemetry / Langfuse, `Observability:Agents:EnableSensitiveData` toggle | Mirrors the WakeCap agent observability convention. |

## 3. High-level features (what it does)

1. **Load** `9_HISTORICAL_DATA` via ClosedXML (row-5 header; drop the junk `AC_Cumul` block) into typed
   `CostCentrePeriod` records, **retaining the raw ordered panel** — `NOT STARTED`/zero-earned rows are kept
   for lag/adjacency and excluded only during pairing/scoring (§6.2/§6.3), not dropped at load.
2. **Build transition pairs**: features at period `p` → label = `AMBER at p+1`, restricted to rows
   `GREEN at p` (the GREEN→AMBER flip).
3. **Engineer features**: current-period EVM + trend deltas (change in CPI and in the budget/progress gap
   over the last 1–2 periods) + resource-split shares + categoricals.
4. **Baselines**: trivial "already-AMBER" (sanity floor) and the **CPI-native set** plus the transparent
   **gap rule** (the default scorer).
5. **Challenger model (stretch)**: ML.NET `FastTree` on the transition.
6. **Rolling-origin evaluation** on a single **top-k ranking contract**: `precision@k`, **false-alerts-per-
   cycle**, per-fold counts + fold range (no fragile CI), descriptive lead-time. The **rule is the
   predeclared deployed scorer**; the challenger is reported side by side as **descriptive only** (no
   adaptive gate). Per-origin OOF artifacts + one all-history forecast artifact so no future leaks backward.
7. **Single scoring path** (`WatchlistScoringService`) shared by the API, the copilot tools, and the
   back-test — emits a ranked watchlist with each row's top 2–3 **risk indicators**.
8. **REST API**: `GET /api/v1/watchlist?period=…` and `POST /api/v1/copilot/ask`.
9. **React watchlist UI**: pick a reporting period, see ranked GREEN-centres-about-to-tip, expand a row for
   its risk indicators + a recent CPI/gap sparkline; a separate model-validation panel; a side **Copilot**
   chat panel.
10. **QS Cost Copilot agent** exposing read-only tools (`GetWatchlist`, `GetCostCentreDetail`, `ExplainDrift`,
    `GetEvmSnapshot`) over Microsoft Agent Framework, scoped to reject off-topic questions.
11. **xUnit tests** enforcing the leakage guards (no shuffle, no `p+1` leakage, correct pair construction)
    and the data contract.

## 4. Architecture / data flow

```
Tower_X_Project_Data.xlsx (9_HISTORICAL_DATA)
        │  ExcelPanelLoader (Infrastructure, ClosedXML)   # row-5 header; keep RAW ordered panel (sentinels→missing)
        ▼
   IReadOnlyList<CostCentrePeriod>  (Domain records, raw)
        │  FeatureBuilder (Core)                          # lag/trend from raw panel, THEN eligibility+pairing (p→p+1)
        ▼
   transition pairs  X_t → y_{t+1}   (GREEN-at-p; AMBER=+, GREEN/CLOSED=−, NOT STARTED=excluded)
        ├───────────────► BaselineScorers (Core)   (rule w/ continuous RuleRiskScore + CPI-native)
        ├───────────────► ChallengerModel (Core)   (ML.NET FastTree, stretch)
        │                       │
        │  RollingOriginEvaluator (Core)  ◄──────────┘    # walk origin; top-k metrics; train-only selection
        ▼                                                 #   → one per-origin ModelArtifact each (cutoff-keyed)
   WatchlistScoringService (Core)  ── artifactFor(period) → ranked Watchlist (+ RiskIndicators)
        ├──────────────► WatchlistController (Web.API)     ──► React watchlist
        ├──────────────► QsAnalyticsTools  ──► ClaudeQsCostCopilotAgent (MAF) ──► CopilotController ──► React chat
        └──────────────► validation-summary (frozen out-of-fold report; model-level, not per-period)
```

## 5. Proposed solution structure (code lands here when built; not created in this task)

```
QsEarlyWarning.sln
src/
  QsEarlyWarning.Domain/                 # entities, value objects, EVM identities — no deps
    Entities/CostCentrePeriod.cs
    ValueObjects/EvmSnapshot.cs          # CV/CPI/SPI/EAC/VAC identities
    ValueObjects/TransitionPair.cs
    ValueObjects/WatchlistRow.cs
    Constants/EvmThresholds.cs           # CPI_THRESHOLD=0.95, TOPK, MIN_TRAIN_ORIGIN, CHALLENGER_MARGIN_PP
  QsEarlyWarning.Infrastructure/         # I/O adapters
    Excel/ExcelPanelLoader.cs            # ClosedXML read + data-contract guards
    Excel/IPanelLoader.cs
  QsEarlyWarning.Core/                   # analytics services (the substance)
    Features/FeatureBuilder.cs           # BuildPairs(), EngineerFeatures()
    Scoring/BaselineScorers.cs           # AlreadyAmber, CpiNative set, GapRule + FitThreshold
    Scoring/ChallengerModel.cs           # ML.NET FastTree train/predict + importances
    Scoring/IWatchlistScorer.cs
    Scoring/WatchlistScoringService.cs   # the single shared path → WatchlistRow[]
    Evaluation/RollingOriginEvaluator.cs # walk-forward folds, top-k metrics, train-only selection, per-origin artifacts
    Evaluation/Metrics.cs                # PrecisionAtK, FalseAlertsPerCycle, LeadTime
    Agent/IQsCostCopilotAgent.cs         # swappable agent interface (Core owns the contract)
    Agent/QsAnalyticsTools.cs            # [Description]-decorated read-only tool methods
  QsEarlyWarning.Agent/                  # Microsoft Agent Framework implementation
    ClaudeQsCostCopilotAgent.cs          # ChatClientAgent + AIFunctionFactory tools (clone of ClaudeTrainingCenterAgent)
    Prompts/CopilotPrompts.cs
    QsEarlyWarning.Agent.csproj          # refs Microsoft.Agents.AI(.Anthropic)
  QsEarlyWarning.SharedKernal/           # DTOs shared API↔agent
    Dto/WatchlistRowDto.cs
    Dto/CopilotAskRequest.cs / CopilotAskResult.cs
  QsEarlyWarning.Web.API/                # ASP.NET Core host
    Controllers/WatchlistController.cs
    Controllers/CopilotController.cs
    Program.cs                           # DI: loader, scorer, agent, IChatClient (Anthropic)
    appsettings.json                     # DataPath, Anthropic model, Observability toggles
tests/
  QsEarlyWarning.Tests/                  # xUnit
    DataContractTests.cs                 # AMBER≡CPI<0.95, 173 BCC_ID, junk block filtered
    TransitionPairTests.cs               # label==1 iff GREEN(p)&AMBER(p+1); no p+1 leakage
    NoLeakageTests.cs                    # rolling-origin train periods strictly < test origin
    CopilotToolScopeTests.cs             # tools are read-only; off-topic questions rejected
frontend/
  qs-early-warning/                      # React micro-frontend (mirrors frontend-2.0/packages/web/pm)
    src/app/Watchlist/                   # ranked table, period picker, driver expander, sparkline
    src/app/Copilot/                     # chat panel calling POST /copilot/ask
    src/api/                             # axios + react-query hooks
    package.json                         # react 18, @tanstack/react-query 4, @wakecap/frontend-components
README.md
```

## 6. Deep implementation

### 6.1 `Domain` — entities, EVM identities, constants
- `CostCentrePeriod` record: `BccId`, `PeriodId`, `Discipline`, `PackageCode`, `AlertLevel`, and the raw EVM
  columns needed to *derive* every identity — at minimum `BacAed`, `PvAed`, `EvAed`, `AcAedCumulative`,
  `EacAed` (plus the pre-computed `Cpi`, `Rolling3mCpi`, `Spi`, `VariancePct`, `EacVsBacRatio`,
  `PctBudgetConsumed`, `ActualPctComplete`, `EarnedQtyCumul`, `AcMaterial/Manpower/Equipment/Subcontract`).
  **Do not** try to build `EvmSnapshot` from `Cpi` alone — the earlier field list was incomplete.
- **Workbook invariant (assert once, cite `data/README.md`):** in this workbook the `AC_AED_Period`
  column is **cumulative**, i.e. `AcAedPeriod == AcAedCumulative`. Map it to `AcAedCumulative` explicitly;
  never treat it as current-period spend. All resource-share numerators must use the **same cumulative
  basis** as the denominator.
- `EvmSnapshot` value object. **Distinguish true EVM identities from forecasting formulas:**
  - *Identities* (always hold): `Cv = Ev − Ac`, `Cpi = Ev / Ac`, `Spi = Ev / Pv`, `Vac = Bac − Eac`
    (guard `Ac==0`, `Pv==0`).
  - `Eac` is **not** an identity — `Eac = Bac / Cpi` is only *one* forecasting assumption. Carry the
    workbook's **raw `EacAed`** as the source of truth and expose the CPI-derived `Eac` separately (label
    it as the CPI-method estimate); never silently substitute one for the other.
  - Used by the `GetEvmSnapshot` copilot tool and any derived display; **never** used to fabricate the
    withheld budget/EV sheets.
- **Verify the workbook's own formulas before assuming EVM relationships** (do not assume
  `PctBudgetConsumed = Ac/Bac` or `ActualPctComplete = Ev/Bac` — confirm against `DATA_DICTIONARY.md` /
  actual cells). The `gap ≈ −Cv/Bac` equivalence only holds if those two definitions hold; assert it in
  `EvmIdentityTests` rather than stating it as fact.
- `EvmThresholds` static: `CpiThreshold = 0.95m`, `TopK = [5, 10]`, `SelectionK = 5`, `MinTrainOrigin = 4`,
  `RandomSeed = 0`. (**No `ChallengerMarginPp`** — the rule is predeclared, there is no adaptive gate or
  dedicated held-out set, so a decision-margin constant has no defined dataset. The rule-vs-challenger
  difference is reported **raw**, per-fold and macro, descriptively.)

### 6.2 `Infrastructure.ExcelPanelLoader` (ClosedXML)
- Open the workbook read-only; select `9_HISTORICAL_DATA`; treat **row 5** as the header.
- Keep rows where `Package_Code` starts with `EP-` (drops the junk `AC_Cumul` block, ~rows 2078–2090).
- Drop `Alert_Level == "NOT STARTED"` and `Earned_Qty_Cumul <= 0` rows for pairing, **but keep the raw
  ordered panel** (pre-filter) so the pair builder can compute lag features and detect real period gaps
  (see 6.3). Parse cells with invariant culture; coerce numerics; sort by `(BccId, PeriodId)`.
- **Two distinct validation layers — do not conflate them:**
  - **Production schema/semantic validation (loader, fail loud):** required sheet/header present; expected
    columns parse; `(BccId, PeriodId)` unique; `BccId` non-blank; `PeriodId` in range; `AlertLevel` ∈
    permitted set. These survive a legitimate workbook refresh.
  - **Sentinel handling — `"-"` is valid, not malformed.** This workbook writes `"-"` for CPI and related
    metrics on `NOT STARTED` (and other not-applicable) rows. Parse a known sentinel (`"-"`, blank) as
    **missing**, not as a parse error. Require **finite numerics only for rows/fields that are eligible for
    pairing or scoring** (live GREEN/AMBER rows); reject Excel error cells / NaN / Inf **only there**. A
    sentinel on a not-applicable row must pass; malformed numeric text on a live row must fail.
  - **Snapshot regression assertions (test fixtures only, `DataContractTests`):** the exact `173 BccId`
    count and 0-mismatch `AMBER ⇔ Cpi<0.95` check belong in tests against *this supplied* workbook — **not**
    in the startup loader, or a new cost centre / changed business threshold takes down the API.
- **Failure + reload policy:** a bad workbook must not silently wedge the API. Load once and cache
  (singleton) for the demo; a read-only health endpoint reports load state, and a **separate authenticated
  `POST /api/v1/admin/reload`** performs a validated, atomic panel+artifact swap that *refuses* an
  incompatible workbook rather than half-applying it (full contract in 6.9).

### 6.3 `Core.Features.FeatureBuilder`
- `BuildPairs(rawPanel)`: per `BccId`, form a pair only where the successor satisfies
  **`next.PeriodId == current.PeriodId + 1`** — an *explicit* period-number match, **never** "the next
  surviving row after filtering" (filtering out `NOT STARTED`/zero-EV rows must not manufacture false
  adjacency across a gap). Compute lag/trend features from the raw ordered panel, *then* apply the
  eligibility rules below.
- **Eligible-population + label contract (no undefined "live"):**
  - **Current row** eligible iff `AlertLevel(p)=="GREEN"` and it is a scoreable row (finite features per
    6.2). Only GREEN-at-`p` centres are the population at risk of the flip.
  - **Positive** (`y=1`): successor `AlertLevel(p+1)=="AMBER"`.
  - **Negative** (`y=0`): successor `AlertLevel(p+1) ∈ {GREEN, CLOSED}` — e.g. the three
    `GREEN(11)→CLOSED(12)` cases are known non-AMBER outcomes and count as negatives, not drops.
  - **Excluded (and counted):** successor `NOT STARTED` / missing / invalid (e.g. the lone
    `GREEN(10)→NOT STARTED(11)`); no successor at all (final period). Report **per-fold exclusion counts**
    so exclusions are visible, not silent.
- `EngineerFeatures(pair)` — features at `p` only:
  - Level: `Cpi`, `Rolling3mCpi`, `Spi`, `VariancePct`, `EacVsBacRatio`,
    `gap = PctBudgetConsumed − ActualPctComplete`, `distTo095 = Cpi − 0.95`.
  - Trend (per `BccId`, using periods ≤ p only), computed **only across exact consecutive periods**:
    `dCpi1 = Cpi(p) − Cpi(p−1)` and `dGap1 = gap(p) − gap(p−1)` require an **exact `p−1` predecessor** that
    is a scoreable row; `dCpi2 = Cpi(p) − Cpi(p−2)` requires an exact `p−2`. If the required predecessor
    period is **missing, `NOT STARTED`, or sentinel-valued**, the delta is emitted **missing** (never
    differenced across a gap) — the same discipline as pair adjacency (§6.3), applied to the lag side.
  - Resource shares: `Ac{Material,Manpower,Equipment,Subcontract} / AcAedCumulative` (guard divide-by-zero;
    same cumulative basis per 6.1).
  - Categoricals: `Discipline`, `PackageCode` (one-hot for ML.NET; string keys for the rule).
- **Feature-collinearity caveat (documented, not silently ignored):** several of these are likely
  near-restatements of CPI — *if* the workbook uses `EAC = BAC/CPI` then `EacVsBacRatio ≈ 1/CPI`, and *if*
  `PctBudgetConsumed = AC/BAC` and `ActualPctComplete = EV/BAC` then `gap ≈ −CV/BAC`; `VariancePct` may
  re-express the same cost variance. These are **conjectures to confirm against the actual columns**, not
  assertions. To the extent they hold, expect the tree to mostly relearn CPI and add little over the
  transparent rule — which **strengthens the rule-first decision** and is a reason not to over-trust
  challenger "sophistication." Run a quick pairwise-correlation check in phase 3 and record it.
- **Trend availability + missing-value policy:** a centre's first (and for `dCpi2`, second) observation has
  no delta and no rolling history. Define the policy explicitly for *both* scorers: the rule treats a
  missing delta as "no trend signal" (does not fire on it); ML.NET receives it as a real missing value
  (HistGradientBoosting-style NaN handling is not automatic in FastTree — impute + add a missing-indicator
  column, or drop first-observation rows from *training* only, documented).
- **Unknown / sparse categoricals (deterministic, fitted-on-train, persisted):** early folds will not have
  seen every `PackageCode`; `PackageCode` may also near-identify `Discipline`. ML.NET's ordinary one-hot
  maps an unseen value to an all-zero vector — it does **not** give you an `"__unknown__"` bucket for free.
  So build an explicit preprocessing step: fit the category vocabulary + minimum-frequency grouping **on the
  training fold only**, fold rare/unseen values into a real `"__unknown__"` level, and **persist that
  vocabulary in the challenger's own evaluation artifact** (§6.6b role (d) — *not* the rule artifact, which
  carries no categorical pipeline) so scoring reproduces it exactly. This only exists when the challenger
  (S1) is enabled. Confirm both fields are actually available at scoring time.
- **Leakage rule (enforced in `TransitionPairTests`):** never attach any `p+1` column except the label;
  trend deltas use only periods ≤ p. A structural "no `p+1` field" check is necessary but **not
  sufficient** — a precomputed feature could embed the future; back it with hand-calculated fixtures (6.11).

### 6.4 `Core.Scoring.BaselineScorers`
- `AlreadyAmber` → **non-ranked sanity check only**, reported *outside* the comparative top-k metrics. On a
  GREEN-at-`p` population it flags nothing (AMBER-at-`p` is excluded by construction), so it has no score and
  is **not** placed in the top-k ranking (where an all-ties row could otherwise catch positives via the
  `BccId` tie-break and muddy the "catches 0 by construction" claim). It exists to justify the framing, not
  to compete in the ranking.
- `CpiNative` → **three distinct** ranking comparators: `Cpi` (lower = riskier — this *is* the
  `distTo095` ordering, so the two are **not** listed separately), `dCpi1` (more negative = riskier), and
  `Rolling3mCpi` trend. (Ranking by `Cpi` and by `−distTo095 = −(Cpi−0.95)` is the identical ordering; only
  one is kept.)
- **The rule needs a continuous score, because the product is a *ranked* watchlist, not a yes/no flag —
  and that score is FROZEN as a predeclared spec, not an example.** `RuleRiskScore` (v1, fixed before any
  evaluation, stored in the artifact):
  - `RuleRiskScore = w_gap · clamp01((gap − x*) / gap_scale) + w_cpi · cpiProximity`, with
    **`w_gap = 0.7`, `w_cpi = 0.3`**, `clamp01(z) = min(1, max(0, z))`.
  - **`cpiProximity` is proximity-from-*above*, not distance below 0.95** — the population is GREEN, where
    `Cpi ≥ 0.95`, so a "distance below" term would be identically zero. Risk is **maximal at the 0.95
    boundary** and decays as CPI rises above it: `cpiProximity = clamp01(1 − (Cpi − 0.95) / cpi_band)` with
    **`cpi_band = 0.10`** (so `Cpi = 0.95` → 1; `Cpi = 0.96` → 0.9; `Cpi ≥ 1.05` → 0; monotone
    non-increasing). This is the corrected component (the old `(0.95 − Cpi)` form was identically zero).
  - **Units are percentage points (pp), matching the workbook's 0–100 pct columns.** `gap =
    Pct_Budget_Consumed − Actual_Pct_Complete` is in pp (e.g. `20.80 − 20.58 = 0.22 pp`, not `0.0022`);
    `x*` and `gap_scale` are in the **same pp units**. `Cpi`/`cpi_band` are unitless ratios.
  - `gap_scale` = the **training-fold** IQR of `gap` in pp (fixed `5 pp` fallback if degenerate) — computed
    on `p < o` only. `cpi_band` is a fixed constant (not fit).
  - **`x*` (gap zero-point) — exact objective:** `FitThreshold` sweeps the **declared pp grid**
    `x ∈ {0, 1, 2, …, 20} pp` and picks the `x` maximizing **macro-mean (across training cycles) of
    `precision@5`** — **k = 5 is the single frozen selection-k** (k = 10 is reported but never selects `x*`);
    tie-break = **smallest `x`**. Computed **on training folds only** (`p < o`); it never sees the reporting
    fold. Grid, selection-k, aggregation (macro-per-cycle), and weights are frozen up front — **no manual
    iteration against eval numbers** (that leaks).
- **Operating contract = ranking (committed).** Both rule and challenger emit a continuous score; they are
  compared at **identical top-k capacity** (§6.6). There is **no separate threshold-vs-recall gate**.
- `CpiNative`/`GapRule` remain the **spine / default scorer** and need no ML runtime.

### 6.5 `Core.Scoring.ChallengerModel` (ML.NET FastTree)
- `Train(pairs)` → `MLContext(seed: 0)` pipeline: one-hot categoricals + missing-value handling (6.3) +
  `Concatenate` features → `FastTree` binary trainer.
- **Hyperparameters must be set, not defaulted** — FastTree defaults overfit tiny folds. Constrain
  `numberOfLeaves`, `minimumExampleCountPerLeaf`, and `numberOfTrees` to small values. Any tuning is
  time-aware and train-only.
- **Zero-positive *training* fold — one frozen behavior:** if the training prefix `p < o` has no positive
  pair, the challenger for that origin is **not trained** — it produces **no artifact and no ranking**, and
  that origin's challenger result is reported **N/A** (never a manufactured/constant-score ranking). The
  rule is unaffected (it needs no positives to score). This keeps the side-by-side fold report honest.
- `PredictProba(row)` → FastTree emits a Platt-calibrated `Probability`. **Class weighting for imbalance
  distorts calibration**, so a weighted score is a *ranking* signal, not a literal transition probability;
  do not present it as "X% chance to flip" without a calibration check.
- **Per-row explanations do NOT come from PFI, and are not called "drivers".** Permutation Feature
  Importance is a **global/dataset-level** diagnostic — it cannot say which features pushed *one* FastTree
  prediction. And the rule-style reason codes are **contextual observations, not a causal attribution of the
  model's score**. So the row-level field is named **`RiskIndicators`** (deterministic phrases computed from
  the row's own observed threshold/trend conditions), *not* `TopDrivers`. If a true per-prediction
  attribution is ever wanted, it needs a real local-explanation method (out of hackathon scope). Keep PFI
  only as a global "what the model leans on" view, clearly labelled as global.

### 6.6 `Core.Evaluation.RollingOriginEvaluator`
- For origin `o` in `[MinTrainOrigin … 11]`: **train** on pairs with feature-period `p < o`, **test** on
  pairs with `p == o` (predicts the `o→o+1` flip). Walk `o` forward; **no shuffling** — splits are by period
  so a centre's future never trains its past.
- **Single operating contract = top-k ranking (no competing threshold gate):**
  - **Ranking metric:** `precision@k` (k ∈ {5,10}) from each period's continuous-score ordering, with a
    **deterministic tie-break** (`RiskScore` desc, then `BccId`). **Denominator is explicit:**
    `precision@k = TP / min(k, eligibleCount)`; always report the effective `kEffective = min(k,
    eligibleCount)` and the fold's positive count. Never pad to k.
  - **Alert set:** "flagged = in the period's top-k" — this single definition drives **TP/FP/FN, recall,
    false-alerts-per-cycle**, so rule and challenger are always compared at **identical alert capacity**.
    There is no second threshold-vs-recall gate.
  - **Zero-positive folds:** a fold with no true GREEN→AMBER flip has **recall = N/A** (not 0) and is
    **excluded from the macro-recall average**; precision and false-alerts-per-cycle stay well-defined
    (precision@k = 0 if k are flagged and none flip). Report how many folds were recall-excluded.
- **Uncertainty is reported honestly, not with a fragile CI.** Origins `o ∈ [MinTrainOrigin=4 … 11]` give
  **8 evaluation folds** (the panel has 11 possible transitions but only 8 are scored as folds), far too few
  for a meaningful two-way cluster bootstrap, so **no bootstrap-CI claim**: report **per-fold counts, macro
  (mean-of-folds), the fold *range* (min/max), and the worst-cycle row**. A single large period must not
  dominate a pooled number. (If a CI is ever added it must specify the crossed-cluster resampling scheme,
  resample count, and zero-positive-fold handling — otherwise it is theatre.)
- `lead-time` is **descriptive only** and near-trivial here (a fixed one-step target has ~1-period nominal
  lead); report it as such, do not dress it up as a survival metric.
- **The deployed scorer is predeclared, NOT chosen from the eval periods — this is the whole point.** With
  11 periods there is no unbiased way to *both* pick rule-vs-challenger on the folds *and* report those same
  folds as performance. So we **do not run an adaptive adoption gate at all**:
  1. **The rule is the deployed scorer, decided up front.** Its only fitted parameters (the `gap` zero-point
     `x*` and `gap_scale`) are set by **inner selection on training periods only** (`p < o`) per origin —
     never on the reporting fold. The weights and `cpi_band` are fixed `RuleRiskScore@v1` constants, not fit.
  2. **The challenger is descriptive/exploratory only (stretch S1).** Every fold reports rule *and*
     challenger `precision@k` **side by side** as a comparison, but the challenger is **never adopted** — the
     rule ships regardless. The rule-vs-challenger difference is reported **raw** (per-fold and macro),
     descriptively; there is **no decision-margin constant** and no gate.
  3. Because nothing is selected on the folds, the rolling-origin numbers are an **unbiased backtest of the
     one deployed (rule) scorer** — no selection bias to disclaim.
  4. If someone later wants to genuinely *switch* to the challenger, that is a separate, pre-registered
     decision made before seeing the final periods — out of scope here; the hackathon ships the rule.

### 6.6b Model artifact + serving lifecycle
Because the **deployed scorer family is predeclared (the rule)**, there is no "which scorer" decision to
leak. The **deployed** artifacts (a)–(c) all run the *same* rule scorer, differing only in training cutoff;
the challenger, when enabled, gets **its own separate, clearly-marked evaluation artifacts (d)** — never
mixed into the deployed rule artifacts:

- **(a) Per-origin OOF rule artifacts — the honest backtest.** For each origin `o ∈ [MinTrainOrigin … 11]`,
  a rule artifact trained **strictly on `p < o`**, using only decisions available at `o` (its own
  inner-selected `x*`, its own train-fold `gap_scale`; `cpi_band` is a fixed constant). These produce the out-of-fold rule
  predictions and are inherently leakage-safe. `artifactFor(periodId)` returns the one whose
  `trainingCutoffPeriod == periodId`. (The rule carries **no** categorical pipeline — its score is numeric.)
- **(b) One production forecast rule artifact — the live watchlist.** Trained on **all eligible historical
  pairs through the last period with a known successor** (feature-periods `≤ 11`), then used to score the
  **latest observed feature period** (p12, which has no successor yet) for the genuinely forward-looking
  watchlist. **Its training step is explicit:** same feature pipeline + same `RuleRiskScore` procedure, fit
  on the full eligible history; frozen with `trainingCutoffPeriod = 12`. This is the artifact a QS acts on.
- **(c) Validation summary — keyed to scorer + version.** The frozen OOF report from (a) describes the one
  deployed rule scorer over the whole rolling-origin evaluation. Stamped with `{ scorer, scorerVersion,
  featureSchemaVersion, evaluationRange }` so it can never be mistaken for a different scorer's numbers.
- **(d) Challenger evaluation artifacts — descriptive, separate, only when S1 is enabled.** Per-origin
  challenger artifacts (the FastTree pipeline + its **persisted train-fold category vocabulary**, §6.3) that
  produce the *descriptive* challenger OOF predictions and the challenger slice of the validation summary.
  They are **versioned and reproducible but never deployed** and never resolved by `artifactFor` for
  serving. Their reproducibility (fixed seed + persisted vocabulary) is what matters, not persistence to the
  serving path.

- `RuleArtifact = { role: OOF|Forecast, trainingCutoffPeriod, scorer="rule",
  scorerVersion="RuleRiskScore@v1", featureSchemaVersion, trainingPrefixFingerprint, ruleZeroPoint(x*),
  gap_scale, cpi_band, weights(w_gap=0.7, w_cpi=0.3) }` — **`scorerVersion` identifies the exact formula** (bump
  it if the equation changes), and `trainingPrefixFingerprint` hashes the `p < c` rows the artifact was fit
  on (used by reload validation). No `categoryVocabulary` — the rule is numeric. `ChallengerArtifact`
  (role (d)) additionally carries `{ categoryVocabulary, fastTreeParams, seed }` and is tagged
  `deployed=false`.
- **`trainingCutoffPeriod` is an *exclusive* scoring origin** (avoids off-by-one): an artifact with cutoff
  `c` is trained on pairs with feature-period `p < c` and scores feature-period `c`. So OOF artifacts have
  `c ∈ [MinTrainOrigin … 11]` (each carries its period-`c` outcome metrics); the **forecast artifact has
  `c = 12`**, trained on `p < 12`, scoring p12 — and it carries **no outcome metrics** (p12's successor
  p13 does not exist yet). The forecast artifact is a **separate post-evaluation fit step**, not one of the
  evaluator's OOF models.
- **Serving:** a retrospective period → its matching OOF artifact (labelled *historical/out-of-fold* in the
  UI); the latest period → the production forecast artifact (labelled *live forecast*). A well-formed period
  with no matching artifact → **404** (per the §6.9 status contract; a malformed/out-of-range `period` is
  the 400 case), never a silent fallback to a future-trained model.
- `ScorePeriod` loads the matching artifact; it never trains implicitly at request time. The frozen
  validation summary (c) is the only source of the KPI numbers shown in the UI (6.7 / 6.9), labelled
  *historical backtest*, never "live".

### 6.7 `Core.Scoring.WatchlistScoringService` (the single shared *scoring* path)
- `ScorePeriod(panel, periodId)` → resolves `artifactFor(periodId)` (6.6b) then returns
  `IReadOnlyList<WatchlistRow>` `{ BccId, Discipline, PackageCode, RiskScore, RiskIndicators }` for the GREEN
  centres in that period, sorted desc by `RiskScore` (deterministic tie-break per 6.6). Rejects a period
  with no matching-cutoff artifact (no future-trained fallback).
- `RiskIndicators` = the 2–3 **deterministic reason codes** for the row (rule: which condition(s) put it
  high; challenger: same row-level observations, *not* PFI, *not* a claim of causal model attribution)
  rendered as plain phrases ("spending 18% ahead of progress; CPI down 3 months").
- **Scoring and back-test reporting are separated.** `ScorePeriod` produces the live watchlist and must not
  compute `PrecisionAtK()` / `FalseAlertsPerCycle()` — those need **future labels** and are undefined for
  the current period. Metric numbers come from the **frozen out-of-fold validation summary** on the model
  artifact, surfaced as *historical validation* (6.9). Both paths share the same feature/scorer code, but
  the service does not become a stateful metrics oracle.
- Divergence honesty: sharing this code **reduces** but does not eliminate cross-surface differences —
  different period, k, artifact version, or rounding still differ. The claim is "same `artifactFor(period)` +
  same k → same rows", not "numbers can never diverge".

### 6.8 `Agent` — Microsoft Agent Framework copilot (clone of `ClaudeTrainingCenterAgent`)
- `Core/Agent/IQsCostCopilotAgent.cs`: **`Task<CopilotAskResult> AskAsync(string question,
  IReadOnlyList<CopilotTurn> history, CancellationToken ct)`** — async + cancellable end to end. `CopilotTurn`
  is a **plain Core DTO** `{ Role, Text }`, **not** the framework's `AgentMessage` — otherwise Core would
  reference Microsoft Agent Framework and the interface would not be genuinely provider-swappable. The Agent
  project translates `CopilotTurn` ↔ framework messages. Core owns the contract (same discipline as
  `ITrainingCenterAgent`) but takes on **zero MAF dependency**.
- `Core/Agent/QsAnalyticsTools.cs`: read-only methods, each `[Description]`-decorated, all backed by
  `WatchlistScoringService` + the model artifact:
  - `GetWatchlist(int periodId, int topK)` — ranked GREEN-about-to-tip centres.
  - `GetCostCentreDetail(string bccId, int periodId)` — one centre's EVM + trend history.
  - `ExplainDrift(string bccId, int periodId)` — the deterministic reason codes for its risk score.
  - `GetEvmSnapshot(string bccId, int periodId)` — CV/CPI/SPI/EAC/VAC from `EvmSnapshot`.
  - **Every tool validates and clamps its own args** (period range, `topK` cap, known `bccId`) and returns a
    typed error, not an exception — the tool boundary is the real authorization/enforcement surface.
- `Agent/ClaudeQsCostCopilotAgent.cs` mirrors `ClaudeTrainingCenterAgent`:
  - Ctor takes `IChatClient` (Anthropic, registered once in `Program.cs`), `QsAnalyticsTools`,
    `ILogger`, `IConfiguration`; reads `Observability:Agents:EnableSensitiveData` (default false).
  - **`BuildAgent(...)` is a WakeCap-private helper, not a framework API** — it must be **copied from
    `ClaudeTrainingCenterAgent` source** (with its transitive deps: the tool-call tracker, scope-rejection
    wrapper, OTel span setup). Referencing the Agent Framework package alone does **not** provide it; budget
    time to port it.
  - Wires tools via `AIFunctionFactory.Create(_tools.GetWatchlist)` etc. into a `ChatClientAgent`, and runs
    with `ChatClientAgentRunOptions`. **Conversation state:** confirm how `history` reaches the model —
    current Agent Framework guidance uses a reusable `AgentSession`/thread, so either replay the capped
    history into the run or hold a session; the earlier `session: null` shorthand does not carry history by
    itself. History capped (`MaxHistoryTurns = 10`).
  - **Scope rejection is defence-in-depth, not enforcement:** a system prompt cannot *guarantee* off-topic
    refusal. Real enforcement is the read-only tool surface + arg validation above; the prompt is a soft
    layer. Tests assert the code-level enforcement, not just a stubbed prompt.
  - **Error handling:** timeout + `CancellationToken`, retry/rate-limit on the Anthropic call, map tool
    exceptions and malformed tool args to typed errors, cap tokens, and a clear fallback when Anthropic is
    unavailable (the watchlist API stays up regardless).
- `CopilotAskResult` returns a **sanitized evidence/citation DTO** (which tool, which period/centre, the
  numbers cited) — **not** raw framework tool-call objects, which could leak internals or sensitive prompt
  data.
- `CopilotPrompts.cs`: the system prompt (role = QS cost analyst; must ground every claim in a tool result;
  never invent figures for the withheld budget/EV sheets).

### 6.9 `Web.API`
- `Program.cs` DI: singleton `IPanelLoader` → cached panel; `WatchlistScoringService`; model artifact
  provider; `QsAnalyticsTools`; `IQsCostCopilotAgent → ClaudeQsCostCopilotAgent`; `IChatClient` built from
  the Anthropic package. **Secrets never live in `appsettings.json`** — the Anthropic key comes from
  environment / user-secrets; the copilot endpoint fails with a clear message *only when enabled* and the
  key is absent (the watchlist works without it). Asp.Versioning; CORS for the React app.
- **All endpoints async + validated, with one status contract:** **`400`** = malformed / out-of-range input
  (non-numeric or out-of-range `period`, bad `k`, oversized history/question, malformed `bccId`);
  **`404`** = well-formed but not found (valid `period`/`bccId` that has no row or no matching artifact).
  Propagate `CancellationToken`.
- `WatchlistController`: `GET /api/v1/watchlist?period={id}&k={5|10}` → `WatchlistRowDto[]` for that period
  (via `artifactFor(period)`; malformed `period`/`k` → **400**; valid period with no artifact → **404**).
- **KPI provenance — one model-level validation panel, not a per-period number.** The frozen backtest KPIs
  (macro `precision@k`, fold range, false-alerts-per-cycle) describe the *model over the whole rolling-origin
  evaluation*; they do **not** vary with the selected live period. Serve them from a **separate**
  `GET /api/v1/validation-summary` (labelled *historical backtest*, with evaluation range + scorer version),
  and have the UI show them as a fixed panel — **not** beside the period picker as if they were that period's
  live accuracy. (This removes the earlier contradiction where §8 implied KPIs "match for the same period".)
- `HealthController`: `GET /api/v1/health` → **read-only** load state, row/centre counts, workbook + active
  model-artifact version/fingerprint.
- **Reload (stretch, S3) is a separate, guarded mutation:** `POST /api/v1/admin/reload` —
  **authenticated + rate-limited** (it mutates server state; never bundled into the public health GET).
  Correct semantics: re-read the workbook, validate **dataset fingerprint + feature schema**, then
  **rebuild *all* artifacts** (per-origin OOF + the forecast artifact, §6.6b) from the new panel and
  **atomically swap panel + freshly-built artifacts together** — never keep an old artifact against a new
  panel. When the challenger (S1) is enabled, reload **also regenerates the challenger OOF artifacts (d) and
  the challenger slice of the validation summary**, so the descriptive comparison never lags the rule.
  (The impossible check "no data postdates a cutoff" is dropped: newer data always exists past older
  per-origin cutoffs; that is normal, the artifacts are rebuilt, not reused.) On any validation failure it
  **refuses activation** and retains the last-known-good snapshot, surfacing the error via health. For the
  hackathon, immutable startup-load is an acceptable substitute for the whole endpoint.
- `CopilotController`: `POST /api/v1/copilot/ask` `{ question, history }` → `CopilotAskResult` (answer +
  the **sanitized** evidence DTO, not raw tool-call objects).

### 6.10 `frontend/qs-early-warning` (React, mirrors `frontend-2.0/packages/web/pm`)
- `src/api/`: axios client + `@tanstack/react-query` hooks (`useWatchlist(period)`, `useAskCopilot()`).
- `src/app/Watchlist/`: period selector; sortable table (`@wakecap/frontend-components`) of risk score,
  centre, discipline, **risk indicators**; per-row expander with indicator detail + recent CPI/gap
  sparkline. A **separate, clearly-labelled *"model validation (historical backtest)"* panel** shows the
  fixed model-level KPIs from `GET /validation-summary` — deliberately *not* rendered as the selected
  period's live accuracy.
- `src/app/Copilot/`: chat panel posting to `/copilot/ask`, rendering the answer and the **sanitized
  evidence citations** (which tool / period / centre / numbers) — not raw framework tool-call objects.
- Structured as a single-spa-compatible package (`root.component.tsx`, `wakecap-fe-*.tsx`) so it can later
  mount inside `frontend-2.0`; standalone Vite dev host for the hackathon demo.

### 6.11 `tests/QsEarlyWarning.Tests` (xUnit)
Structural assertions are necessary but not sufficient; back the important ones with **hand-calculated
fixtures** so a wrong computation (not just a wrong shape) fails.
- `DataContractTests` (**fixture/snapshot** tier, this workbook only): 173 `BccId`; only `EP-` rows survive;
  `AMBER ⇔ Cpi<0.95` on live rows; junk block excluded. (Production schema validation is tested separately —
  see 6.2 — so a workbook refresh does not break these.)
- `LoaderRobustnessTests`: missing file/sheet/header, duplicate `(BccId, PeriodId)`, blank `BccId`, Excel
  error cells / NaN/Inf **on a live row**, out-of-range period → typed failures, not crashes. **Plus the
  positive case:** a `"-"` sentinel on a `NOT STARTED` row parses as *missing* and is accepted (distinguish
  allowed sentinel from malformed numeric on a live row).
- `TransitionPairTests`: label defined exactly as GREEN(p)&AMBER(p+1); population restricted to GREEN(p);
  no `p+1` fields; **plus** fixtures for the full outcome contract (§6.3): a **`GREEN(p)→CLOSED(p+1)`** case
  asserted as a **negative** (`y=0`, not dropped), a **`GREEN(p)→NOT STARTED(p+1)`** case asserted
  **excluded and counted**, a **missing intermediate period** and a **`NOT STARTED` row between two live
  rows** proving no false adjacency, and a **final-period** row with no successor; hand-calculated
  `dCpi1/dGap1` values. **Lag boundary fixtures:** `dCpi1/dGap1` require an exact `p−1` predecessor and
  `dCpi2` an exact `p−2` — assert they are emitted **missing** (not differenced across a gap) when the
  predecessor period is absent / `NOT STARTED` / sentinel-valued.
- `EvmIdentityTests`: CV/CPI/SPI/EAC/VAC computed from source fields match hand values; divide-by-zero and
  the `AcAedPeriod == AcAedCumulative` invariant asserted.
- `RuleScoreTests` (guards the frozen `RuleRiskScore@v1`, §6.4): **CPI component = 1 at `Cpi=0.95`, strictly
  between 0 and 1 just above the boundary, 0 at/above `0.95 + cpi_band`, and monotone non-increasing** as CPI
  rises (this is the test that would have caught the identically-zero bug); `gap` computed in **pp** from
  hand values (`20.80 − 20.58 = 0.22`); `x*` selection uses **k=5 macro-per-cycle** on training folds only;
  a full-score hand fixture matches.
- `NoLeakageTests`: every rolling-origin fold's train periods strictly `< test origin`; **`RuleRiskScore`
  parameter fitting (`x*` and `gap_scale`) sees training folds only** (assert the fit input never includes
  the reporting fold); **the weights and `cpi_band` are the fixed `RuleRiskScore@v1` constants** (assert
  their values, not that they are estimated); the **descriptive challenger never controls deployment**
  (assert the deployed scorer is always the rule regardless of challenger numbers); deterministic tie-break
  makes precision@k reproducible.
- `ArtifactLifecycleTests`: an artifact with cutoff `c` trains **only** on `p < c`; OOF artifacts exist for
  origins **4–11**; the separately-fitted **cutoff-12 forecast artifact carries no outcome metrics**;
  an incompatible period/artifact pair fails; **identical input + artifact yields deterministic scores**;
  `scorerVersion` matches the frozen formula and `trainingPrefixFingerprint` matches the `p < c` rows.
- `MetricsTests`: precision@k / TP-FP-FN / false-alerts against fixed fixtures, including a **period with
  fewer than k eligible centres**, a **final period** with no successor, and a **zero-positive fold**
  (recall = N/A, excluded from macro-recall; precision/FP-per-cycle still defined). Do **not** assert live
  empirical benchmark numbers in integration tests (those live in a separate report, versioned by
  dataset + model).
- `CopilotToolScopeTests`: every `QsAnalyticsTools` method is side-effect-free and clamps its args; the
  **code-level** enforcement (tool arg validation / typed errors) is what's asserted, not a stubbed prompt.

## 7. Build phases (sequence when implemented)

**Demo-critical path (freeze the demo here — this is the guaranteed deliverable):**
1. **Solution + data contract**: create the `.sln` and projects; `ExcelPanelLoader` + schema validation +
   `DataContractTests`/`LoaderRobustnessTests` green.
2. **Pairs + features**: `FeatureBuilder` + `TransitionPairTests` + `EvmIdentityTests` (confirm the workbook
   formulas, run the collinearity check).
3. **Rule + eval harness + artifacts**: `BaselineScorers` (the frozen `RuleRiskScore@v1`),
   `RollingOriginEvaluator` (top-k contract, fold-range reporting). **Produce both artifact sets:** the
   **per-origin OOF rule artifacts (4–11)** *and* the **post-evaluation cutoff-12 forecast artifact** (§6.6b
   (a)+(b)); `NoLeakageTests`/`ArtifactLifecycleTests`/`MetricsTests` green; emit the first rolling-origin
   report for the CPI-native set and gap rule.
4. **API + table**: `Program.cs` DI + `WatchlistController` (`artifactFor(period)`) + `validation-summary` +
   read-only `health`; React watchlist table + the labelled validation panel against the running API.

**Stretch backlog (independently removable — build only if the demo-critical path is done and valid; each
must not regress it):**
- **S1 Challenger**: `ChallengerModel` (ML.NET FastTree) + persisted category vocabulary; run the
  **predeclared descriptive comparison** (challenger reported side by side) — *the rule ships regardless*;
  the challenger never controls deployment.
- **S2 Copilot**: `CopilotTurn`/`QsAnalyticsTools`, ported `ClaudeQsCostCopilotAgent`, `CopilotController`,
  `CopilotToolScopeTests`, sanitized evidence DTO. (First thing cut if the timebox slips.)
- **S3 Ops/polish**: authenticated `admin/reload` with atomic swap; observability (named exporters +
  redaction); single-spa mount compatibility.
- **S4 README** with run commands (`dotnet run`, `pnpm dev`) and the Anthropic key setup (env/user-secrets).

## 8. Verification (end-to-end)

- `dotnet test` → all data-contract, pair, leakage, and tool-scope tests pass.
- `dotnet run --project src/QsEarlyWarning.Web.API` then `GET /api/v1/watchlist?period=…` returns the ranked
  watchlist (via `artifactFor(period)`); `GET /api/v1/validation-summary` reports the **deployed rule's**
  macro `precision@k` vs the **CPI-native comparators** (`Cpi`, `dCpi1`, `Rolling3mCpi` trend), with
  **fold-by-fold counts and the fold range** (no bootstrap-CI claim). (No separate "gap baseline" is
  listed — the deployed `RuleRiskScore` *is* the gap-based rule, so it cannot be a baseline it competes
  against.) The challenger's numbers appear **side by side as descriptive context**, not as an adoption
  decision — the deployed scorer is the rule regardless.
- `POST /api/v1/copilot/ask` `{ "question": "which centres are about to drift and why?" }` → an answer
  grounded in `GetWatchlist`/`ExplainDrift` tool calls; an off-topic question is refused; the Anthropic key
  being absent degrades only the copilot, not the watchlist.
- Frontend: the watchlist renders for a selected period, top rows show GREEN centres with **risk
  indicators**; the model-validation panel shows the fixed backtest KPIs and does **not** change with the
  selected period (it is model-level, not period-live); the copilot cites the same figures the table shows.
- Sanity (evidence, not proof): **predeclare** the example transitions to inspect, or show *all* out-of-fold
  predictions for a fold — do not cherry-pick one "known" transition after the fact.

## 9. Out of scope / notes
- No RED class, no multi-class severity, no cross-project claims (single project).
- No Python at build or runtime — the entire analytics core is C#.
- `WatchlistScoringService` is the single *scoring* path; the copilot's tools and the API controller both
  depend on it, so the same (period, k, artifact) yields the same rows — see 6.7 for the honest divergence
  caveat (this is not "numbers can never diverge").
- **Provenance caveat:** the workbook is organiser-generated/reconciled; observed patterns may reflect the
  generation formulas rather than real construction behaviour. Every result is framed as *exploratory
  single-project evidence*, not a validated forecast.
- **Cut-line / build priority (per codex sequencing):** the **guaranteed deliverable is the rule-first
  watchlist + a valid rolling-origin back-test + one API + one React table** (phases 1–5, 7). The
  **FastTree challenger, the copilot, single-spa mount compatibility, and Langfuse** are *independently
  removable* — build them only after the rule-first result is valid. The copilot is a deliberate product
  choice (the point of this ASP.NET + MAF revision), but it is explicitly the first thing cut if the
  timebox slips; it must never block the core watchlist.
- The React package is kept single-spa-compatible so it can later mount inside `frontend-2.0`, but the
  hackathon deliverable runs it standalone (two hosting modes = extra debugging; standalone Vite is the
  demo path, mount compatibility is best-effort).
- The agent reuses WakeCap's Microsoft Agent Framework pattern (pinned `Microsoft.Agents.AI` +
  `Microsoft.Agents.AI.Anthropic` prerelease, Claude-backed `IChatClient`, interface-swappable, OTel
  sensitive-data toggle) — but note `BuildAgent(...)` and the tracker/scope wrappers are **copied WakeCap
  source**, not framework APIs (6.8). Observability names concrete packages/exporters + redaction rules
  before it is claimed, not just "OTel / Langfuse".

## Codex Review — reconciliation log

Two independent codex passes were run on this plan (an initial 10-finding pass, then a broader 40-finding
pass). Their substantive findings are now **folded into the plan body above**; this log records where each
lands so a re-review can verify closure. Findings are grouped by theme, not by original number.

### Folded in — statistical validity (the core risk)
- **No selection-on-test-folds; report, don't auto-select** → §1 Goal, §6.6 (fixed procedure: inner
  train-only selection + predeclared held-out periods; rule is the guaranteed ship).
- **Non-IID / 74 centres, 11 steps; a 5-pt margin ≈ one event** → §1 Goal guardrail, §6.6. **Bootstrap CI
  dropped** (11 clusters make it theatre): report per-fold counts + fold range + worst-cycle instead.
- **Threshold fitting leaks** → §6.4 (predeclared target recall, train-folds only).
- **Feature collinearity — several features are algebraic restatements of CPI** (`EacVsBacRatio ≈ 1/CPI`,
  `gap ≈ −CV/BAC`) → §6.3 caveat; strengthens the rule-first decision.

### Folded in — EVM / data correctness
- **`EvmSnapshot` missing source fields** (`BAC/PV/EV/AC-cumulative/EAC`) → §6.1 record widened.
- **`AC_AED_Period` is cumulative in this workbook** → §6.1 invariant, §6.3 resource-share basis.
- **Explicit successor matching (`PeriodId+1`), no false adjacency after filtering** → §6.3 + §6.11 fixtures.
- **Trend availability / missing-value policy; unknown+sparse categoricals** → §6.3.
- **Divide-by-zero + Excel error/`"-"`/NaN handling** → §6.1, §6.2, §6.11 `LoaderRobustnessTests`.

### Folded in — serving lifecycle & honest metrics
- **Model artifact + train-before-serve lifecycle** (was undefined) → new §6.6b.
- **PFI is global-only; per-row drivers = deterministic reason codes** → §6.5, §6.7.
- **Back-test metrics ≠ live outputs; label as historical** → §6.7, §6.9 KPI provenance.
- **Ranking vs alert metrics are separate contracts; `<k` denominator; deterministic tie-break** → §6.6.
- **Success criterion was self-contradictory ("beat baselines" incl. the gap baseline)** → §1 + §8 now
  "report whether", not "confirm".
- **Data-contract split: production schema validation vs fixture snapshot assertions** → §6.2, §6.11.

### Folded in — agent, API, security
- **Agent version corrected** to `Microsoft.Agents.AI.Anthropic 1.3.0-preview.260423.1` (prerelease) → §2.
- **`BuildAgent(...)` is copied WakeCap source, not a framework API** → §6.8, §9.
- **Async + cancellable `AskAsync`; input validation → 400/404; conversation-state/session handling** → §6.8,
  §6.9.
- **Scope rejection is defence-in-depth; real enforcement = read-only tools + arg validation** → §6.8, §6.11.
- **Agent error handling (timeout/retry/rate-limit/fallback); sanitized evidence DTO, not raw tool calls** →
  §6.8, §6.9.
- **Secrets out of `appsettings.json` (env/user-secrets); copilot-absent degrades only chat** → §6.9.
- **Health/reload endpoint; startup-cache staleness** → §6.2, §6.9.
- **Provenance: organiser-generated workbook** → §1 guardrail, §9.

### Judged out-of-scope (deliberate, with rationale)
- **"Cut the copilot / too much surface for a hackathon."** The ASP.NET + React + **MAF copilot** is the
  intent of this revision, not accidental scope creep. Kept, but demoted to the **explicit cut-line** (§9):
  rule-first watchlist + valid back-test + one API + one table is the guaranteed deliverable; challenger,
  copilot, single-spa mount, and Langfuse are independently removable. This honours codex's *sequencing*
  without dropping the product goal.
- **Observability specifics** (exact OTel exporters/redaction) are named as a requirement (§9) but not fully
  designed here — acceptable for a hackathon plan, flagged so it is not claimed as done.

_Next: re-run `/codex review` against this reconciled plan to confirm the folded findings hold and surface
anything new._

### Codex re-review — 2026-07-06

**Verdict:** the first-pass findings are substantively addressed. The plan is ready to implement after the
remaining contradictions below are resolved; items 1–4 affect correctness or architecture, while items
5–6 are consistency clean-up.

1. **The loader currently rejects values that legitimately occur in the raw panel.** Section 6.2 says to
   retain the pre-filter panel for adjacency/lag logic while also rejecting non-numeric `"-"` cells. The
   supplied workbook uses `"-"` for CPI and related metrics on `NOT STARTED` rows, so strict raw-row
   validation would reject the workbook before filtering. Parse known sentinels as missing for statuses
   where the field is not applicable; require finite numeric values only for rows/features that are
   eligible for pairing or scoring. Update `LoaderRobustnessTests` to distinguish an allowed sentinel from
   malformed numeric data on a live row.
2. **"Both endpoints must be live" leaves the target population ambiguous and can bias period 11.** The
   workbook has three `GREEN(p=11) → CLOSED(p=12)` cases; these are known non-AMBER outcomes and should
   normally be negatives for a watchlist scored at period 11. It also has one unusual
   `GREEN(p=10) → NOT STARTED(p=11)` case that needs an explicit data-quality policy. Define eligible
   outcomes precisely: for example, current row must be eligible GREEN; successor AMBER is positive;
   successor GREEN/CLOSED is negative; successor NOT STARTED/invalid is excluded and reported. Add fixture
   tests and per-fold exclusion counts. Do not use the undefined word "live" as the filtering contract.
3. **Historical period selection requires an artifact registry, not one artifact.** Section 6.6b correctly
   requires training data strictly before the served period, but `ScorePeriod(..., artifact)` and the UI's
   free period picker imply one frozen artifact can serve every period. An artifact refit through period 11
   would leak when serving period 6. Either (a) build/load an artifact keyed by scoring cutoff for each
   historical period, or (b) expose historical rows explicitly as stored out-of-fold predictions and use a
   single latest artifact only for the current period. Include `artifactVersion` and `trainingCutoffPeriod`
   in every response and reject incompatible period/artifact combinations.
4. **The supposedly framework-neutral Core contract still exposes `AgentMessage`.** If that is the
   Microsoft Agent Framework type, `QsEarlyWarning.Core` must reference the framework and the implementation
   is not actually swappable as claimed. Define a Core-owned `CopilotMessage`/history DTO (role + sanitized
   text), then map it to framework messages or sessions inside `QsEarlyWarning.Agent`.
5. **Reload must be atomic and access-controlled.** Do not place a state-changing reload operation behind
   `GET /health`. Use a separate authenticated/admin-only `POST /reload` (or keep reload CLI-only for the
   demo), validate the workbook and compatible artifact completely, then atomically swap the snapshot.
   On failure retain the last-known-good snapshot and expose the error via health. This prevents requests
   from observing mismatched panel/model versions.
6. **Earlier summary sections still contradict the reconciled design.** Update §3 item 6, the architecture
   diagram, `EvmThresholds`, and build phase 4, which still describe an unconditional five-point "margin
   gate" and freezing the scorer directly in `WatchlistScoringService`. They should reference train-only
   selection with uncertainty and the versioned artifact/provider. Also change §3 item 1/the diagram so
   they do not imply destructive filtering before lag and successor construction.

The two-way by-period/centre bootstrap also needs a concrete algorithm before coding. With only 11 period
clusters, treat its interval as descriptive sensitivity analysis, report the raw fold results alongside
it, and avoid implying that the interval repairs the dataset's limited external validity.

### Codex third review — 2026-07-06

**Verdict: the preceding re-review is still unresolved in the operative plan.** No new substantive issue
supersedes those findings, but the claimed update has not changed the relevant sections:

- §6.2 and §6.11 still reject `"-"` unconditionally even though retained `NOT STARTED` rows legitimately
  contain that sentinel.
- §6.3 still says only "both endpoints must be live" and does not define `CLOSED` or `NOT STARTED`
  successor treatment.
- §6.6b/§6.7 still describe a single artifact while allowing arbitrary historical periods, with no
  cutoff-keyed artifact registry or stored out-of-fold prediction path.
- §6.8 still exposes framework `AgentMessage` in the Core-owned interface.
- §6.2/§6.9 still mention an unspecified reload trigger without a separate authenticated, atomic reload
  contract.
- §3 item 6, the architecture diagram, `EvmThresholds`, and build phase 4 still describe the old margin
  gate/direct scorer-freeze design.

The plan should not be marked reconciled until those six locations are edited in the plan body and their
tests/structure are updated consistently. The detailed corrections remain in the immediately preceding
`Codex re-review — 2026-07-06` section and are not repeated here.

### Round-3 resolution — 2026-07-06 (body now edited)

The six re-review findings above are now folded into the plan body (this closes the "third review"
verdict, which was written before these edits landed):

1. **`"-"` sentinel is valid, not malformed** → §6.2 sentinel-handling bullet (parse `"-"`/blank as missing;
   require finite numerics only on pairing/scoring-eligible rows) + §6.11 `LoaderRobustnessTests` positive
   case.
2. **Successor population defined; word "live" removed** → §6.3 eligible-population contract
   (GREEN-at-`p`; AMBER=positive; **GREEN/CLOSED=negative**; NOT STARTED/missing/final=excluded-and-counted)
   + `GREEN(11)→CLOSED(12)` and `GREEN(10)→NOT STARTED(11)` handled; per-fold exclusion counts.
3. **Per-origin artifact registry, not one artifact** → §6.6b `artifactFor(periodId)` (cutoff-keyed, from the
   rolling-origin models); latest artifact = only true forecast; incompatible period/artifact → 400;
   responses carry `artifactVersion` + `trainingCutoffPeriod`.
4. **Core-owned history DTO, no framework type** → §6.8 `CopilotTurn { Role, Text }` replaces `AgentMessage`;
   Core takes zero MAF dependency; Agent project translates.
5. **Atomic, access-controlled reload** → §6.9 authenticated `POST /api/v1/admin/reload` (validate
   fingerprint+schema+cutoff, atomic swap, retain last-known-good on failure); read-only `GET /health`; §6.2
   updated.
6. **Stale summary sections reconciled** → §3 item 6, the §4 diagram, `EvmThresholds`, and build phase 4
   (now stretch **S1**) no longer describe the unconditional margin gate / direct scorer-freeze; diagram no
   longer implies destructive filtering before lag/successor construction.

Also: the **two-way bootstrap CI is dropped**, not just caveated (§6.6) — with 11 period clusters it is
reported as raw fold results + range, avoiding any implication that an interval repairs external validity.

_Next: re-run `/codex review`; the body should now match every point in the two review sections above._

### Codex fourth review — 2026-07-06

**Verdict:** the six round-3 findings are now substantively resolved. Four narrower inconsistencies remain
before implementation.

1. **The final holdout cannot both choose the winner and provide the headline result.** Section 6.6 calls
   the last `HOLDOUT_PERIODS` untouched, then adopts the challenger based on its performance on those same
   periods. Once used for that decision, they are validation data, not an unbiased final holdout. Given the
   small panel, use nested rolling-origin selection and report the outer folds, or keep the rule as the
   predeclared shipped scorer and report the challenger descriptively without selecting on the headline
   folds. Remove the undefined "per-fold ranges do not overlap in the wrong direction" criterion; a range
   overlap is not a statistical decision rule.
2. **The latest forecast artifact is not created by the stated rolling evaluator.** Evaluation origins end
   at 11 because labels require period 12, but §6.6b says the true forecast uses an artifact with cutoff 12
   to score period 12 → 13 and also says these are "exactly" the evaluator's per-origin models. Add a
   separate post-evaluation fit step that trains the already-selected scorer on `p < 12`, persists the
   cutoff-12 artifact without outcome metrics for period 12, and clearly separates it from retrospective
   artifacts for origins 4–11. Define `trainingCutoffPeriod` as an **exclusive scoring origin** to avoid
   off-by-one interpretation.
3. **The reload compatibility rule is impossible as written.** Section 6.9 requires "that no data postdates
   each artifact's cutoff," but a full 12-period panel necessarily contains data after artifacts for origins
   4–11. Validate instead that each artifact's recorded training-data fingerprint contains no row at or
   after its exclusive cutoff, while allowing the loaded panel to contain later rows. Recompute or reject
   retrospective artifacts whose training-prefix fingerprint no longer matches; validate the cutoff-12
   forecast artifact against the period-`<12` prefix.
4. **Two stale details remain despite the reconciliation claim.** Section 3 item 1 still says the loader
   drops `NOT STARTED` rows before producing `CostCentrePeriod` records; change it to retain the raw panel
   and apply eligibility after lag construction. Also extend `TransitionPairTests` with explicit
   `GREEN→CLOSED` negative and `GREEN→NOT STARTED` excluded cases—the current listed fixture only proves
   that filtering does not manufacture adjacency.

After these edits, the plan's data contract, scoring contract, framework boundaries, and operational
surface are coherent enough to begin the demo-critical implementation path.

### Round-4 resolution — 2026-07-06 (body now edited)

The four fourth-review findings are folded into the plan body:

1. **Holdout no longer both selects and reports** → §1, §3 item 6, §6.6. The **adaptive adoption gate is
   removed entirely**: the rule is the predeclared deployed scorer; the challenger is descriptive-only
   (side by side), never selected on the folds; `ChallengerMarginPp` is a reporting annotation, not a gate;
   the undefined "ranges do not overlap in the wrong direction" criterion is deleted. Backtest is unbiased
   for the one deployed scorer.
2. **Forecast artifact is a defined, separate step** → §6.6b role (b) + the exclusive-cutoff rule:
   `trainingCutoffPeriod` is an **exclusive scoring origin** (cutoff `c` ⇒ train on `p < c`, score `c`);
   OOF artifacts `c ∈ [4…11]` carry outcome metrics; the **forecast artifact `c = 12`** is a post-evaluation
   fit on `p < 12`, scores p12, and carries **no outcome metrics**. It is explicitly *not* one of the
   evaluator's OOF models.
3. **Reload rule made possible** → §6.9: dropped the impossible "no data postdates cutoff" check; reload now
   **rebuilds all artifacts** from the new panel and swaps atomically (each artifact validated against its
   own training-prefix fingerprint; the panel may legitimately hold later rows). Immutable startup-load is
   the acceptable hackathon substitute.
4. **Two stale details fixed** → §3 item 1 now retains the raw panel (eligibility applied after lag
   construction); `TransitionPairTests` extended with explicit `GREEN→CLOSED` negative, `GREEN→NOT STARTED`
   excluded, and final-period fixtures.

_Next: re-run `/codex review`; the selection/holdout, artifact-lifecycle, reload, and summary sections
should now be internally consistent._

### Codex fifth review — 2026-07-06

**Verdict:** all four fourth-review findings are resolved. The plan is architecturally coherent, but one
last implementation-level decision must be frozen before phase 3.

1. **The deployed transparent rule is still underspecified.** Section 6.4 gives only an example
   `RuleRiskScore` ("e.g." normalized gap excess combined with distance to CPI 0.95), while later sections
   call it the predeclared deployed scorer. The plan alternately refers to `CpiNative`, `GapRule`, and the
   rule as though they identify one formula. Freeze the exact score equation, feature direction, scaling
   method, missing-value behavior, coefficient/weight grid (if any), and train-only objective/tie-break for
   choosing `x*` and weights. State whether the deployed rule is gap-only or a gap+CPI composite. Otherwise
   the claimed predeclaration and reproducible per-origin artifacts do not exist.

Required follow-through once that formula is chosen:

- Persist every fitted rule parameter and training-prefix fingerprint in each artifact; `scorerVersion`
  must identify the formula, not just carry a generic name.
- Add `ArtifactLifecycleTests`: cutoff `c` trains only on `p < c`; OOF artifacts exist for 4–11; the
  separately fitted cutoff-12 forecast artifact has no outcome metrics; incompatible period/artifact pairs
  fail; identical input + artifact yields deterministic scores.
- Update `NoLeakageTests` wording from "model selection" to the actual design: rule-parameter fitting is
  train-only and the descriptive challenger never controls deployment.
- In build phase 3 explicitly produce both the OOF artifact registry and the post-evaluation cutoff-12
  forecast artifact; currently the latter is defined in §6.6b but not named in the critical-path phase.

With the rule contract frozen and these tests added, no further plan-level blocker is apparent; remaining
choices can be made during implementation without changing the experiment's meaning.

### Round-5 resolution — 2026-07-06 (body now edited)

The fourth-review findings (1–6) and the fifth-review follow-throughs are folded into the body:

- **#1 `RuleRiskScore` frozen** → §6.4 now specifies the exact equation
  (`0.7·clamp01((gap−x*)/gap_scale) + 0.3·clamp01((0.95−Cpi)/dist_scale)`, a **gap+CPI composite**), the
  train-fold scales, the declared `x*` grid `{0.00…0.20}`, objective (train-fold `precision@k`), tie-break
  (smallest `x`), and "no manual iteration against eval numbers". `scorerVersion="RuleRiskScore@v1"`.
- **#2 challenger artifact separated** → §6.6b adds **role (d)** (challenger-owned, versioned, `deployed=false`,
  holds the category vocabulary); §6.3 points there, not at the rule artifact; reload regenerates (d) when
  S1 is on (§6.9).
- **#3 `AlreadyAmber` scoped out** → §6.4 makes it a non-ranked sanity check, outside the top-k comparison.
- **#4 zero-positive folds** → §6.6 + `MetricsTests`: recall = N/A, excluded from macro-recall; precision/FP
  still defined.
- **#5 fold count** → §6.6 now says **8 evaluation folds** (origins 4–11), not "~11 clusters".
- **#6 status contract** → §6.9: **400** = malformed/out-of-range, **404** = well-formed but not found.
- **Follow-throughs** → `RuleArtifact` carries every fitted parameter + `trainingPrefixFingerprint` and a
  formula-identifying `scorerVersion`; new **`ArtifactLifecycleTests`**; `NoLeakageTests` reworded to
  rule-parameter fitting (train-only) + challenger-never-deploys; **build phase 3 names both the OOF registry
  and the cutoff-12 forecast artifact**.

_Codex verdict at this point: "essentially ready after resolving findings 1–3 … no further plan-level
blocker is apparent." Next `/codex review` should confirm._

### Codex sixth review — 2026-07-06

**Verdict:** the fifth-review requests are implemented, but validation of the now-frozen equation found one
blocking mathematical error.

1. **The CPI component is identically zero for the population being scored.** Eligibility requires the
   current row to be GREEN, and the verified business rule is GREEN ⇒ `Cpi >= 0.95`. Therefore the frozen
   term `clamp01((0.95 - Cpi) / dist_scale)` is zero for every eligible row (including exactly 0.95), so
   `w_dist = 0.3` never contributes. `RuleRiskScore@v1` is not the stated gap+CPI composite; it is a scaled
   gap-only score with avoidable ties.

Replace the CPI term with an above-threshold proximity score whose direction is correct, for example:

`cpiProximity = clamp01(1 - ((Cpi - 0.95) / dist_scale))`

This assigns maximum risk at the 0.95 boundary and decays to zero as GREEN CPI moves farther above it.
Freeze the chosen equation and bump the scorer version after the correction. Add boundary tests proving
the CPI contribution is 1 at `Cpi=0.95`, between 0 and 1 just above the boundary, 0 at/above
`0.95 + dist_scale`, and monotone non-increasing as CPI rises.

Two associated details must be made explicit while editing the formula:

- State that `gap`, `x*`, and `gap_scale` use **percentage-point units**. In the workbook, values such as
  `Pct_Budget_Consumed=20.80` and `Actual_Pct_Complete=20.58` produce `gap=0.22`, not `0.0022`.
- Choose a single primary k for fitting `x*` (or an explicit aggregate/tie-break across k=5 and k=10).
  "Maximize training-fold `precision@k`" is ambiguous when both k values are reported and may select
  different thresholds.

The artifact lifecycle and tests requested in the fifth review are otherwise present. Once this score
direction/unit contract is corrected and covered by tests, the plan is ready for implementation.

### Round-6 resolution — 2026-07-06 (body now edited)

The sixth-round findings (the frozen-formula math bug + the associated details) are folded in:

- **CPI component fixed (blocking math bug)** → §6.4: replaced the identically-zero `(0.95 − Cpi)` term with
  **proximity-from-above** `cpiProximity = clamp01(1 − (Cpi − 0.95) / cpi_band)`, `cpi_band = 0.10` — max
  risk at the 0.95 boundary, monotone decreasing, 0 at/above 1.05. `RuleArtifact` weight renamed
  `w_cpi = 0.3`; `scorerVersion` stays `RuleRiskScore@v1` (first frozen version). This was **my own bug**
  from the previous round — caught and corrected.
- **Units made explicit** → §6.4: `gap`, `x*`, `gap_scale` are **percentage points** (`20.80 − 20.58 =
  0.22 pp`); the `x*` grid is rescaled to pp `{0, 1, …, 20}`; `Cpi`/`cpi_band` are unitless.
- **Single selection-k frozen** → §6.4: `x*` is fit to maximize **macro-per-cycle `precision@5`** (k=5 only;
  k=10 is reported, never selects); tie-break smallest `x`. Removes the "which k" ambiguity.
- **Boundary tests added** → §6.11 `RuleScoreTests`: CPI contribution = 1 at 0.95, ∈(0,1) just above, 0 at/
  above `0.95 + cpi_band`, monotone non-increasing; pp-unit `gap`; k=5 train-only selection.
- **Other fifth-round items** (400→404 in §6.6b, S1 adoption language stripped, zero-positive challenger
  training fold = N/A, duplicate `Cpi`/`−distTo095` comparator collapsed to one) also folded in.

_Codex verdict: "Once this score direction/unit contract is corrected and covered by tests, the plan is
ready for implementation." Both are now done. Next `/codex review` should confirm ready._

### Codex seventh review — 2026-07-06

**Verdict: ready for implementation.** The CPI proximity direction is now correct, percentage-point units
and selection-k are explicit, and `RuleScoreTests` cover the failure mode. No plan-level correctness blocker
remains.

Two non-blocking stale references should be cleaned up while implementation starts:

- Remove `ChallengerMarginPp` from `EvmThresholds` and §6.6. The plan now predeclares the rule and has no
  adaptive gate or dedicated held-out periods, so a statement that the challenger "led by N pp on held-out
  periods" has no defined dataset and can mislead an implementer. Report the raw per-fold and macro
  difference descriptively without a decision-margin constant.
- In `NoLeakageTests`, change "parameter fitting (`x*`, scales, weights)" to "parameter fitting (`x*` and
  `gap_scale`)". The weights and `cpi_band` are fixed constants in `RuleRiskScore@v1`; a test must assert
  their fixed values, not imply they are estimated from training data.

These are editorial/test-contract corrections, not reasons to delay phase 1. The demo-critical path can
begin as written.

### Round-7 resolution — 2026-07-06 (body now edited)

Sixth-round findings (target/CLOSED, lag adjacency, gap-baseline label) **and** the seventh review's two
non-blocking cleanups are folded in:

- **Target defined by the AMBER label, not raw CPI** → §1: the modeled target is `AlertLevel(p+1)=="AMBER"`;
  it coincides with `CPI<0.95` on live GREEN/AMBER rows but the label is authoritative so `CLOSED` (negative)
  and `NOT STARTED` (excluded) successors are unambiguous. Resolves the §1↔§6.3 CLOSED contradiction.
- **Lag features require exact predecessors** → §6.3: `dCpi1/dGap1` need an exact `p−1`, `dCpi2` an exact
  `p−2`; a missing/`NOT STARTED`/sentinel predecessor → emitted **missing**, never differenced across a gap.
  Lag boundary fixtures added to `TransitionPairTests`.
- **Phantom "gap baseline" removed** → §8: the deployed rule *is* the gap rule, so it is compared only
  against the **CPI-native comparators** (`Cpi`, `dCpi1`, `Rolling3mCpi`); no separate gap baseline.
- **`ChallengerMarginPp` removed** (seventh review) → §6.1 `EvmThresholds` and §6.6: no adaptive gate or
  held-out set exists, so the decision-margin constant is gone; the rule-vs-challenger difference is reported
  **raw** (per-fold + macro), descriptively.
- **`NoLeakageTests` wording tightened** (seventh review) → fitted params are `x*` and `gap_scale` only;
  weights and `cpi_band` are asserted as fixed `RuleRiskScore@v1` constants, not estimated.

_Seventh-review verdict was already "**ready for implementation**"; these fold in the remaining editorial
items. Next `/codex review` should return clean / ready._

### Codex eighth review — 2026-07-06

**Verdict: clean and ready for implementation.** The operative plan now has a coherent target and
eligibility contract, exact-adjacency lag policy, frozen and directionally correct rule score, leakage-safe
rolling evaluation, explicit artifact lifecycle, deterministic metrics, framework-neutral Core boundary,
and a scoped critical path.

The seventh-review cleanup is verified:

- `ChallengerMarginPp` is absent from the operative constants and evaluation procedure; challenger results
  are descriptive raw differences only.
- `NoLeakageTests` correctly treats only `x*` and `gap_scale` as fitted parameters and asserts the weights
  and `cpi_band` as fixed constants.

Older findings and obsolete formulas remain quoted in the chronological reconciliation log by design;
they are superseded by the later resolution entries and do not describe the implementation contract. No
further plan edit is required before starting phase 1.
