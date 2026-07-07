# Plan — Integrate Idea 3 (Estimate Assumption Stress Test) into the platform

## Context

Idea 3 (`ideas/idea-3-should-cost-auditor.md`, reframed to **Estimate Assumption Stress Test**) rebuilds
every estimate package from norms × rates and surfaces the **aggressive or unusual assumptions baked into
the estimate itself, on day zero** — for a QS to review before a shift is worked. It is a deterministic
engine, **not** an ML model, and it emits **three explicitly separated output classes** that are never
fused into one score:

- **Class 1 — Arithmetic reconciliation tie-out** (correctness proof, *not* a signal). Rebuild resource
  cost from norms × rates with the Output-Norm-corrected quantity math and confirm it ties to BOQ direct
  cost end to end. The dataset reconciles by construction. Ship it as the engine's credibility artifact.
- **Class 2 — Unusual input assumptions** (estimate-side, reads **zero actuals**; the day-zero product).
  Flag for QS review: `Output Norm` in the top percentile of its cohort, `Unit Rate (AED)` at the bottom of
  the resource's plausible band, risky norm `Notes` adjustments (e.g. "−30% confined spaces"), and
  thin/zero contingency. These are **review prompts**, not verdicts.
- **Class 3 — Peer-benchmarked cost risk** (**RETROSPECTIVE VALIDATION ONLY**). Benchmark a package's
  expected unit cost against the *realized actuals of other comparable packages*, leave-one-out,
  **suppressed below 5 eligible peers**. Same-project actuals do not exist at award, so Class 3 **cannot
  run pre-execution** — it ships in a separate, clearly-labelled retrospective panel, never an at-award
  flag. On this single project many/all cells are expected to suppress (see G5); that suppression is
  itself the honest result, not a bug.

**Decisions (confirmed via AskUserQuestion):**
1. **Data source — read workbook sheets 1–4 directly** (ClosedXML, reusing the `ExcelPanelLoader` pattern).
   The Postgres estimate tables are empty (the importer only loads sheet 9) **and** the estimate schema is
   missing the fields Idea 3 needs. Reading the workbook is faithful to the idea's deterministic batch
   engine and needs no schema/importer/migration change. The estimate is **Tower-X only**; the stress test
   is single-project and is bound to the owning project (G8).
2. **Class 3 included, retrospective-only** — gated peer benchmark in a separate retrospective panel.
3. **Full UI** — a "Stress Test" tab: Class-1 tie-out status, a Class-2 assumption heatmap by
   package/discipline (with the driving resource line + flag reasons), and the separate Class-3 panel.

**Save location:** this doc at `plan/idea-3-estimate-assumption-stress-test-integration-plan.md`.

## Actual workbook schema (verified via openpyxl — build against THESE columns)

Header row is **row 4** for sheets 2/3/4. Sheet 1 uses a **two-tier header** — group labels on row 4,
**real sub-headers on row 5** (verified: `Sec`, `Item Ref`, `Description`, `Unit`, `Quantity`,
`Direct+Indirect Unit Cost (AED)`, `Direct+Indirect Amount (AED)`, `Margin %`, `Margin Amount (AED)`,
`Cont %`, `Contingency Amount (AED)`, `TOTAL Unit Price (AED)`, `TOTAL Amount (AED)`, `Norm Ref`); the
sheet-1 loader reads row 5. **`Margin %`/`Cont %` load as percentage *points* (e.g. `22`, `8`), not
fractions** — so the G12 `Cont % < 2` threshold is in points and needs no rescale (verified against the
raw workbook).

- **`1_BOQ`** — per BOQ item: `Sec`, `Item Ref`, `Description`, `Quantity`, `Direct+Indirect Unit`,
  `Direct+Indirect Amount`, `Margin %`, `Margin Amount`, `Cont %`, `Contingency Amount`,
  `TOTAL Unit Price`, `TOTAL Amount` (= Direct+Indirect+Margin+Cont), `Norm Ref`.
- **`2_ESTIMATE_NORMS`** — `Disc Code`, `Discipline Name`, `Sub-Trade Code`, `Sub-Trade Name`, `Norm Code`,
  `Operation / Activity`, `Unit`, `Output Norm`, `Procurement Route`, `Manpower — Gang Composition`,
  `Gang Size`, `Material 1 — Description`, `Mat1 Qty/UoW`, `Mat1 Unit`, `Material 2 — Description`,
  `Mat2 Qty/UoW`, `Equipment 1`, `SC Trade / Notes`. **There is no gang-count / equipment-count column** —
  `Gang Size` + `Output Norm` are the manpower inputs; `Equipment 1` is a free-text description.
- **`3_BOQ_MAPPING`** — `BOQ Sec`, `Item`, `BOQ Description`, `Unit`, `Norm Code`, `Estimate Package`,
  `Op Code`, `Primary Resource Types`, `Procurement`, `Notes`.
- **`4_ESTIMATE_DATASHEET`** — `BOQ Sec`, `Item`, `BOQ Description`, `Unit`, `BOQ Qty`, `Norm Code`,
  `Package`, `Op Code`, `Resource Type`, `Resource Description`, **`Qty/Unit Work`**, **`Consumption Unit`**,
  **`Total Resource Qty`**, **`Unit Rate (AED)`**, **`Resource Cost (AED)`**, **`Indirect Cost (AED)`**,
  **`Total Contract Amt (AED)` [= BOQ col M]** (repeated on every resource row of an item), `Contract Unit
  Price (AED)`, `Gang Output`, `Gang Size`, `Notes`.

Joins: `Norm Code` links norms/mapping/datasheet; `(BOQ Sec, Item)` links to the BOQ; `Estimate Package` =
sheet-9 `Package_Code` (verified 68/68) for Class 3.

## Data & leakage guards (non-negotiable — front-loaded)

- **G0 — numeric tolerances are named constants; `TiesOut` is the conjunction of every check.** All
  Class-1 checks compare with explicit tolerances: **quantity re-derivation** `|recomputed − stored| ≤
  1e-6 × max(1, |stored|)` (relative); **money identities** (`Resource Cost = TRQ × Unit Rate`,
  `DirectTieOutDelta`, `ContractUplift = Margin + Cont`) `|delta| ≤ 0.01 AED` per item, project rollups
  `≤ 1 AED`. **`TiesOut = true` iff *all* hold:** (a) every line's quantity re-derivation within the
  quantity tolerance; (b) every line's `Resource Cost = TRQ × Unit Rate` within the money tolerance;
  (c) every per-item `DirectTieOutDelta` and **`ContractUpliftDelta = ContractUplift − (Margin Amount +
  Contingency Amount)`** within the money tolerance (the *delta* is checked, since `ContractUplift` itself
  is legitimately non-zero); (d) **both**
  project-rollup deltas within 1 AED — `ProjectDirectDelta = Σ_items(Σ(Resource Cost + Indirect Cost)) −
  Σ_items(BOQ Direct+Indirect Amount)` and `ProjectUpliftDelta = Σ_items ContractUplift −
  Σ_items(Margin Amount + Contingency Amount)`; **(e) the repeated `Total Contract Amt` is *consistent*
  across an item's resource rows** (`max − min ≤ 0.01 AED`) — an inconsistent repeat is a data error and
  forces `TiesOut = false`. A failure in any one check flips `TiesOut` to false (never masked by the
  others). Each check is surfaced as its own boolean/delta on the result so a FAIL is always explained
  (G2b). Constants live in one place; tests probe just-inside/just-outside each bound and each failing case.
- **G1 — Output-Norm divisor is load-bearing; validate against stored quantities.** The datasheet already
  stores `Total Resource Qty`, `Resource Cost`, `Indirect Cost`, and `Total Contract Amt`, so Class 1 runs
  at two levels: **(a)** internal arithmetic — `Resource Cost == Total Resource Qty × Unit Rate` per line;
  **(b)** divisor re-derivation — independently recompute `Total Resource Qty` and assert it matches the
  stored column. **Verified against the workbook: the formula is uniform across *all* resource types
  (manpower, equipment, material, subcontract): `Total Resource Qty = BOQ Qty × Qty/Unit Work ÷ Output
  Norm`.** (This is what the data does; it differs from `data/README.md`'s "materials/subcontract scale
  with quantity" wording — the actual rows divide every type by Output Norm.) Dropping the `÷ Output Norm`
  divisor makes recomputed qty ≠ stored qty and the check fails; the tie-out *is* the validator.
- **G2 — Class 1 separates tie-out error from the margin/contingency uplift.** `Indirect Cost` is
  **per-line** (varies per resource row — sum it, do **not** dedup); `Total Contract Amt` is **repeated per
  BOQ item** (dedup it — count once per item). Two distinct quantities per BOQ item, never conflated:
  `DirectTieOutDelta = Σ(Resource Cost + Indirect Cost) − BOQ Direct+Indirect Amount` (must be ≈ 0, the
  correctness proof) and `ContractUplift = TotalContractAmt(dedup) − Σ(Resource Cost + Indirect Cost)`
  (equals `Margin Amount + Contingency Amount` **by construction**, reported as composition, not a signal).
- **G3 — Class 2 reads no actuals.** Every Class-2 feature comes only from sheets 1–4. No sheet-9 / panel
  value may enter a Class-2 flag (enforced by construction: the Class-2 path receives the `EstimateModel`
  only, never the panel). Asserted by a test.
- **G4 — Class 3 tautology guard (hard boundary).** **Target cells are enumerated solely from estimate
  data** (a package's estimated items define its `(unit, resourceType, procurementRoute)` cells); the
  target's own realized actuals never create or qualify its cell. The realized peer pool is built
  **separately** from all packages' completed BCCs, then the **target package is excluded** (strict
  leave-one-out on `PackageCode`) before aggregation/quantiles. A package's own actual/CPI must never enter
  its own benchmark (estimate-vs-own-actual is `CPI` by identity). A test asserts LOO exclusion end to end.
- **G5 — Class 3 peer key is poolable, gated, and honestly suppressible.** Each of the 68 sub-trades maps
  to exactly **one** package here, so a *same-sub-trade* key yields **zero** peers by construction.
  Eligibility is therefore keyed on **(unit of work + resource type + procurement route)**, pooled across
  packages; sub-trade/discipline comparability is an **advisory** annotation, not a hard filter. Publish
  the peer count on every benchmark and **suppress (emit `Suppressed`, fall back to Class 2) below the peer
  minimum `MinPeerN = 5`.** Where the whole workbook yields < 5 peers for every cell, Class 3 ships **fully
  suppressed** and the UI/notes say so plainly. No precision/recall claims.
- **G6 — Class 3 uses completed centres, no cumulative double-count.** `AcCumulative` (=`AC_AED_Period`)
  is **cumulative** in this workbook, so summing period rows double-counts. Order matters: **take each BCC's
  latest row first** (max `PeriodId`), **then** require **that row's `ActualPctComplete >= 100`** — so a
  stale earlier 100% row on a BCC that later regressed/reopened cannot qualify. The eligible latest row's
  resource-type AC (`AcManpower`/`AcMaterial`/`AcEquipment`/`AcSubcontract`) and `Earned_Qty_Cumul` feed the
  package-cell aggregation (G13). Non-completed / zero-earned centres are excluded.
- **G11 — Class-3 attribution is per-BCC via `WbsCode`, sourced from the estimate.** **Verified in the
  data: sheet-9 `WBS_Code` equals the BOQ `Item` ref** (e.g. BCC `BCC-CIV-DEMO-101` → `WbsCode = 1.01`),
  so each completed BCC joins **1:1 to a single BOQ item**. Package-level attribution is wrong here — **21
  of 68 packages span multiple units** (e.g. `EP-CIV-DEMO` = {m², m³, lin.m}). So for each completed BCC,
  match `WbsCode` → the unique estimate BOQ item; derive **unit** (the authoritative `BoqMapping.Unit` from
  sheet 3, cross-checked against `BoqLine.Unit`/`ResourceLine.Unit` — mismatch excludes the item) +
  **procurement route** (item's norm/mapping) + resource type there. The panel supplies **only** realized AC + `Earned_Qty_Cumul`
  (`Package_Code` = `Estimate Package`). Exclude any BCC that does not match exactly one item, or whose
  matched item's unit/route is ambiguous (recorded in `Notes`). Needs **no loader/schema change**
  (decision 1) — `WbsCode` is already on `CostCentrePeriod`.
- **G13 — Class-3 unit-cost math is exact, at the package-cell grain (one observation per package).** The
  peer observation unit is a **package-cell** = `(PackageCode, unit, resourceType, procurementRoute)`, so a
  package with many BCCs contributes **one** observation and cannot inflate `PeerCount` or dominate the
  quantiles.
  - `RealizedUnitCost(cell) = Σ AC_<resourceType> over the cell's eligible completed BCCs (each BCC's
    latest row, G6) ÷ Σ Earned_Qty_Cumul over those BCCs`. Each completed BCC is placed in its cell via the
    `WbsCode`→item unit/route attribution (G11).
  - `EstimatedUnitCost(cell) = Σ(Resource Cost + Indirect Cost) for that resource type over the package's
    items matching the cell's unit **and** procurement route ÷ Σ BOQ item Quantity for those items`
    (per-line indirect added in, like-for-like with realized; both cost and quantity filtered on the same
    unit+route).
  - Peers for a target cell = **other package-cells in other packages** (LOO excludes the target's own
    package, G4) sharing the same `(unit, resourceType, procurementRoute)`. **`PeerCount` = number of
    distinct peer packages.** `PeerMedian = P50`, `PeerBandLow/High = P25/P75` (type-7) computed over those
    **package-level** observations; `DeltaPct = (EstimatedUnitCost − PeerMedian) ÷ PeerMedian × 100`.
  - **Value gates (avoid denominator bias):** a BCC is included when `AC_<rtype>` is **finite and ≥ 0** and
    `Earned_Qty_Cumul` is **finite and > 0** — **zero-AC BCCs are kept** (a legitimate zero cost still
    contributes its earned quantity to the denominator; dropping it would bias the unit cost upward). A
    package-cell observation is used only if its aggregate `Σ Earned_Qty_Cumul > 0` and `Σ AC_<rtype> > 0`
    (an all-zero-AC cell is treated as no cost signal → dropped), and `EstimatedUnitCost` is finite and > 0.
    Suppress the benchmark if surviving `PeerCount < MinPeerN`.
- **G7 — `Notes` rules are review prompts.** `Notes`-derived adjustments (versioned keyword rules) are
  surfaced as review prompts with the matched text, **not** numeric evidence, unless the adjustment logic
  is deterministic and versioned. The rule table is a small, versioned constant.
- **G8 — estimate source is bound to its owning project (by resolved id).** The workbook belongs to
  **Tower X only**. At startup the owning slug (config `Data:EstimateProjectSlug=tower-x`) is resolved
  **once** to its `project_id` via the existing bypass-role `ProjectResolver.ResolveAsync` (no user context
  needed; slug→id is not tenant-sensitive). **Fail closed:** if the slug does not resolve, the owning id is
  null and `TryLoadForProject` always returns null (stress test simply unavailable). The loader returns the
  model **only** when `projectId == owningProjectId`. Because `ProjectSnapshotRegistry.Build` already has
  `projectId`, **no `GetOrBuild/Rebuild` signature change is needed** — the id comparison is inside `Build`.
  The Tower-X estimate is never rendered against a mismatched project.
- **G9 — determinism / rule stability.** The engine is pure and deterministic: same workbook ⇒
  byte-identical report. Class-2 percentile/band cohorts are computed within comparable groups with a
  min-sample gate (G10) and ties broken deterministically, so a single-member cohort cannot flip on
  re-run. Stability is asserted by re-running and comparing.
- **G10 — Class-2 cohorts must be dimensionally comparable, with a min-sample gate.** `Output Norm`
  percentiles are computed within **(sub-trade + unit of work)** cohorts (norms producing the same unit);
  `Unit Rate` bands within **(resource type + resource description + consumption unit)** cohorts. A flag is
  only emitted when its cohort has **≥ `MinCohortN`**; thinner cohorts are suppressed, not guessed. This
  prevents mixing m²/m³/No. or unlike resources into one percentile. `MinCohortN` and `MinPeerN` are
  **non-overridable `const int = 5`** (not config) with boundary tests at 4 (suppressed) and 5 (emitted).
- **G12 — Class-2 thresholds are exact, versioned constants (no free-floating "top percentile").** A
  `RulesVersion = "v1"` constant tags every flag. A cohort's **threshold value** is a quantile computed with
  the **type-7 linear-interpolation method** on the ascending-sorted cohort; **flagging is a value
  comparison**, so all rows with an equal value are treated identically (no identifier tie-break decides
  inclusion — identifiers only order the displayed output, G9). Rules:
  - `OutputNormTopPercentile` — `Output Norm` **≥ P90** of its (sub-trade+unit) cohort (aggressive
    productivity assumption).
  - `UnitRateBottomOfBand` — `Unit Rate` **≤ P10** of its (resource type+description+consumption unit)
    cohort (thin rate).
  - Contingency rules are **mutually exclusive**: `ZeroContingency` iff `Cont % == 0`; `ThinContingency`
    iff `0 < Cont % < 2`.
  These thresholds live as named constants in one place; changing them bumps `RulesVersion`.

## Estimate input model + interface — `src/QsEarlyWarning.Domain/Estimate/` (new)

**Layering (verified):** `Core → Domain + Infrastructure`, `Infrastructure → Domain`, `Domain → nothing`
(and `IProjectPanelSource` lives in Infrastructure). Putting the estimate input records or `IEstimateSource`
in Core would force `Infrastructure → Core` (the loader) — **circular**, since Core already references
Infrastructure. So the shared **input** records + interface live in **Domain** (where `CostCentrePeriod`
already is); the loader is in Infrastructure (→Domain); the engine + result models are in Core (→Domain).

- `EstimateModel.cs` (Domain) — immutable joined estimate records read from the workbook (fields match the
  verified schema above):
  - `EstimateNorm { NormCode, DiscCode, DisciplineName, SubTradeCode, SubTradeName, Unit, OutputNorm, ProcurementRoute, GangComposition, GangSize, Mat1QtyPerUoW, Mat2QtyPerUoW, Notes }`
  - `BoqLine { Sec, ItemRef, Description, Unit, Quantity, DirectIndirectAmount, MarginPct, MarginAmount, ContPct, ContingencyAmount, TotalAmount, NormRef }`
  - `BoqMapping { Sec, ItemRef, Unit, NormCode, EstimatePackage, OpCode, PrimaryResourceTypes, Procurement }`
    — the **authoritative item `Unit`** for G11 cell attribution comes from `BoqMapping.Unit` (sheet 3),
    cross-checked against `BoqLine.Unit`/`ResourceLine.Unit`; a mismatch excludes the item (G11).
  - `ResourceLine { Sec, ItemRef, NormCode, Package, OpCode, ResourceType, ResourceDescription, Unit, BoqQty, QtyPerUnitWork, ConsumptionUnit, TotalResourceQty, UnitRate, ResourceCost, IndirectCost, TotalContractAmt, GangOutput, GangSize }`
  - `EstimateModel { Norms, BoqLines, Mappings, ResourceLines }` + lookups by `NormCode`, `(Sec,ItemRef)`,
    `Package`.
- `IEstimateSource.cs` (Domain) — `EstimateModel? TryLoadForProject(long projectId)` (G8).

## Core engine — `src/QsEarlyWarning.Core/StressTest/` (new)

- `EstimateStressTester.cs` — `Run(EstimateModel estimate, IReadOnlyList<CostCentrePeriod>? panel) → StressTestReport`:
  - **Class 1 (G1, G2):** per `(Sec,ItemRef)` — verify `Resource Cost ≈ Total Resource Qty × Unit Rate`;
    recompute `Total Resource Qty = BOQ Qty × Qty/Unit Work ÷ Output Norm` (uniform, all types) and
    reconcile to the stored value (the divisor proof); **sum** per-line `Indirect Cost`, **dedup** `Total
    Contract Amt` per item; emit `ReconciliationResult { Scope, QuantityReDerivationOk, ResourceCostIdentityOk, RepeatedContractAmtConsistent, DirectTieOutOk, ContractUpliftOk, DirectCost, IndirectCost, DirectTieOutDelta, TotalContractAmt, ContractUplift, ContractUpliftDelta, TiesOut, AbsPct, Failures }` per item/package + a project rollup carrying `ProjectDirectDelta` + `ProjectUpliftDelta` (G0). `Failures` is a list of `ReconciliationFailure { Scope, Check, Line?, Actual, Expected, Delta, Tolerance }` — the offending line/item detail behind each false conjunct, exposed through the DTO for the banner.
  - **Class 2 (G3, G7, G10, G12):** cohort-relative flags with exact thresholds — `OutputNormTopPercentile`
    (≥P90 within sub-trade+unit), `UnitRateBottomOfBand` (≤P10 within resource type+description+consumption
    unit), `RiskyNotes` (versioned keyword rules → prompt), `ThinContingency` (`Cont %` <2%) / `ZeroContingency`.
    Cohorts gated by `MinCohortN`. Emit `AssumptionFlag { Package, Discipline, SubTrade, Unit, ResourceType, Kind, Severity, Reason, CohortN, RulesVersion, DrivingResourceLine, EstimatedUnitCost }`.
  - **Class 3 (G4, G5, G6, G11, G13):** per **package-cell** `(PackageCode, unit, resource type,
    procurement route)` — completed BCCs attributed to cells via `WbsCode`→item (G11) and aggregated to one
    observation per package (G13) — gather eligible peer **package-cells in other packages** (LOO, G4)
    sharing (unit, resource type, procurement route), apply finite/positive gates, compute
    `EstimatedUnitCost` / `RealizedUnitCost` and the P25/P50/P75 peer band over package-level observations
    (G13), emit `PeerBenchmark { Package, Unit, ResourceType, ProcurementRoute, SubTradeAdvisory, EstimatedUnitCost, PeerMedian, PeerBandLow/High, PeerCount, DeltaPct, Status }` (`PeerCount` = distinct peer packages; `Status ∈ {Benchmarked, Suppressed}`; `Suppressed` when `PeerCount < MinPeerN` or panel absent). Unmatched/ambiguous BCCs excluded (G11).
  - Report: `StressTestReport { Available, GeneratedForProject, Reconciliation, AssumptionFlags, PeerBenchmarks, PackageHeat, Class3NoCellMeetsMinPeers, Notes }`. `Class3NoCellMeetsMinPeers` is true iff no
    cell reached `MinPeerN` (cells with 1–4 peers still publish their actual `PeerCount`, they are not
    "0 peers"). `PackageHeat` aggregates Class-2 severity per package × discipline for the heatmap grid.
- `StressTestModels.cs` — the records above (positional, non-finite `double?` sanitized). No new NuGet deps.

## Infrastructure — estimate workbook reader

- `IEstimateSource` lives in **Domain** (see above), mirroring how `CostCentrePeriod` is shared; it returns
  the model only for the owning project id, resolved once from `Data:EstimateProjectSlug` at startup (G8).
- `src/QsEarlyWarning.Infrastructure/Excel/EstimateWorkbookLoader.cs` — `IEstimateSource` impl (→Domain) reading
  sheets 1–4 via ClosedXML, reusing `ExcelPanelLoader`'s `Num()`/`Str()` sentinel parsing and
  case-insensitive header→column map. Header row = 4 for sheets 2/3/4; sheet 1's two-tier header is handled
  by scanning rows 4–6 for the row carrying the real sub-headers. Column names matched case-insensitively
  with newline/whitespace normalized (headers contain embedded `\n`, e.g. `Qty/\nUnit Work`). Loads once
  and **memoizes** by path. Missing workbook / sheet / non-owning project id ⇒ returns `null` (stress test
  degrades to unavailable; the snapshot is unaffected).
- DI (`Program.cs`): register `IEstimateSource` as a singleton bound to config `Data:WorkbookPath` +
  `Data:EstimateProjectSlug` (default `tower-x`); the owning `project_id` is resolved once at startup via
  the existing bypass-role `ProjectResolver.ResolveAsync` (fail closed to null), and `TryLoadForProject`
  gates on it (G8).

## Registry integration — `ProjectSnapshotRegistry`

Inject `IEstimateSource`. In `Build(...)` (which already has `projectId`), after the forecaster block, add
a graceful-degradation block mirroring it — **no signature change** to `GetOrBuild/Rebuild`:

```
StressTestReport? stressTest = null;
try {
    var estimate = _estimate.TryLoadForProject(projectId);  // null unless this is the owning project (G8)
    if (estimate is not null)
        stressTest = new EstimateStressTester().Run(estimate, panel);   // panel → Class 3 only
} catch { /* stress test unavailable; leave null */ }
```

Hang `StressTest` on `ProjectSnapshot` (new nullable property, documented like `Forecaster`). Cached
per-project, rebuilt on `RebuildAsync`, never a DB path. Class 1+2 depend only on `estimate`; Class 3 uses
`panel` (the RLS-scoped, per-project actuals) joined by `Package_Code` = `Estimate Package`.

## API — `src/QsEarlyWarning.Web.API/Controllers/StressTestController.cs` (new), route `api/v1/stress-test`

Reuse `DashboardController`'s tenant `Resolve` (registry + `ProjectDirectory` + per-request RLS probe;
401/403/404). New DTOs in `Contracts/StressTestDtos.cs` (positional records; non-finite doubles → null).
All endpoints read `snapshot.StressTest` and return `{ available:false }` (200) when null, so the tab
renders a clean empty state:

- `GET /stress-test/reconciliation` → Class 1 tie-out status + per-item/package `DirectTieOutDelta` +
  `ContractUplift` composition (the correctness proof).
- `GET /stress-test/assumptions?discipline=` → Class 2 flags + `PackageHeat` grid (the day-zero heatmap).
- `GET /stress-test/peer-benchmark` → Class 3 retrospective benchmarks with peer counts + suppression,
  explicitly tagged `retrospective:true`, plus `class3NoCellMeetsMinPeers`.

## Frontend — new "Stress Test" tab

- `src/api/client.ts`: add `stressReconciliation/stressAssumptions/stressPeerBenchmark` + types.
- `App.tsx`: add a `stress` tab to the `Tab` union + `TABS` + render block.
- `components/StressTest.tsx`:
  - **Class 1 banner** — green/amber pill: "Reconciliation ties out to the AED (uplift = margin +
    contingency)" or a red FAIL. On FAIL, render each `ReconciliationFailure` (check, offending line/item,
    actual, expected, delta, tolerance) for every failed conjunct — quantity re-derivation, `Resource Cost`
    identity, `DirectTieOutDelta`, `ContractUpliftDelta`, or an inconsistent repeated `Total Contract Amt` —
    plus the two project-rollup deltas. Never a bare unexplained FAIL. Framed as *correctness proof, not a
    signal*.
  - **Class 2 heatmap** — package × discipline grid, cells shaded by assumption-flag severity (reuse
    `.tag`/`.pill`/`.kpi` CSS); a detail table listing each flag's kind, reason, cohort N, the **driving
    resource line**, and estimated unit cost.
  - **Class 3 retrospective panel** — clearly labelled "RETROSPECTIVE — not an at-award flag"; table of
    benchmarked packages with **actual peer count**, band, delta %, and a visible suppressed (<5 peers)
    state; if `class3NoCellMeetsMinPeers`, show the honest "No cell meets the 5-peer minimum on this
    single-project workbook" message (peer counts of 1–4 are still shown, not reported as "0").
  - Reuse the inline `Spark`/SVG idiom and the "measured, not asserted" honesty note from
    `ForecastBacktest.tsx`.

## Verification

1. **Unit tests** (`tests/QsEarlyWarning.Tests`, xUnit; workbook via `TestData.WorkbookPath`):
   - **Reconciliation tie-out (credibility artifact):** `DirectTieOutDelta = Σ(Resource Cost + Indirect
     Cost) − BOQ Direct+Indirect Amount ≈ 0` across items, and `ContractUplift == Margin Amount +
     Contingency Amount` (within the G0 tolerances). Recomputed `Total Resource Qty = BOQ Qty × Qty/Unit
     Work ÷ Output Norm` matches the stored column for every resource type — asserts G1; drop the divisor
     and it fails.
   - **G0 tolerances:** boundary tests probe just-inside/just-outside each bound (quantity rel 1e-6; money
     0.01 AED/item; rollup 1 AED) so `TiesOut` flips exactly at the threshold.
   - **G2/G0 conjunction:** per-line `Indirect Cost` summed (not deduped); `Total Contract Amt` counted
     once per BOQ item (rollup total = BOQ Σ `TOTAL Amount`); `TiesOut` flips to false when **any** single
     check fails — a bad line quantity, a broken `Resource Cost` identity, a per-item delta out of bound,
     either project-rollup delta (`ProjectDirectDelta` / `ProjectUpliftDelta`) > 1 AED, or an
     **inconsistent repeated `Total Contract Amt`** (one regression fixture per case). Each failing case
     sets its own result boolean **and** emits a `ReconciliationFailure` with actual/expected/delta/tolerance
     (asserted on the DTO); `ContractUpliftDelta` (not the raw uplift) is the value tested.
   - **G4 leakage:** target cells are enumerated from estimate data only; a package's Class-3 benchmark
     excludes its own `PackageCode` from the realized peer pool (LOO) — asserted end to end.
   - **Round-6 regressions (explicit fixtures):** (a) a BCC whose latest row is 80% is excluded even though
     an earlier row hit 100%; (b) a zero-AC BCC's `Earned_Qty_Cumul` stays in a mixed cell's denominator
     (unit cost not inflated); (c) `EstimatedUnitCost` numerator **and** denominator both exclude items on
     a different procurement route.
   - **G5/G6/G11/G13:** `PeerCount` counts **distinct peer packages**, and a package with many BCCs yields
     one package-cell observation (a duplicated-BCC fixture must not inflate the count or move the median);
     cells with `PeerCount < MinPeerN (5)` return `Suppressed` (boundary at 4 vs 5); Class-3 unit cost uses
     each eligible (`ActualPctComplete >= 100`) BCC's latest `PeriodId` row (no cumulative double-count);
     the `WbsCode`→BOQ-item join is 1:1 and multi-unit/ambiguous BCCs are excluded;
     `EstimatedUnitCost`/`RealizedUnitCost` and the P25/P50/P75 band match a hand-computed cell;
     non-finite/≤0 observations are dropped.
   - **G3:** Class-2 flags identical with/without a panel supplied.
   - **G9/G10/G12:** two runs produce identical flags; cohorts with `< MinCohortN (5)` emit no flag (boundary
     at 4 vs 5); rows with equal cohort values are flagged identically; `ZeroContingency` and
     `ThinContingency` are mutually exclusive.
   - Keep the existing suite green.
2. Build: `dotnet build QsEarlyWarning.sln`; `cd frontend/qs-early-warning && npx tsc --noEmit`.
3. Run API (`:5070`) + dashboard (`:5173`) against `qs_phase1`; `curl` each endpoint with
   `-H "X-User-Id: 1" -H "X-Project-Slug: tower-x"` → 200; **non-member `X-User-Id: 2` → 403**; bad project
   → 404. Confirm the reconciliation ties out and the heatmap payload is populated.
4. Browser (Playwright MCP): open the **Stress Test** tab; confirm the Class-1 banner, the Class-2 heatmap +
   detail, and the clearly-labelled Class-3 retrospective panel (likely fully suppressed) render;
   screenshot; only benign console noise. Fix any real bug and re-verify.
5. **Report honestly:** the tie-out PASS is the headline correctness result; Class 2 is review prompts;
   Class 3 is a weak, explicitly single-project retrospective indicator with published peer counts, likely
   fully suppressed on this workbook.

## Out of scope / guardrails

- **No schema/importer/migration change** (decision 1). DB-backed estimate data is a separate future plan.
- Class 3 stays **retrospective**; a true day-zero benchmark needs completed **prior-project** peers,
  absent here.
- No precision/recall vs `CPI < 1` headline (it is `CPI` by identity); QS-review + rule-stability only.
- `Notes` adjustments remain review prompts (G7); no auto re-price / auto-contingency (deferred).
- No cross-project rate library, no Monte-Carlo priced band (deferred, per the idea's CEO review).

## Codex Review

### Round 1 (2026-07-07) — blocking findings

1. G1 contradicted the workbook: all resource types compute qty from `BOQ Qty × Qty/Unit Work ÷ Output
   Norm`; `GangCount`/`EquipmentCount` do not exist; `ResourceLine` omitted `Qty/Unit Work` and indirect
   cost. Fix the model/formula and include indirect cost in the tie-out.
2. Class 1 conflated zero tie-out error with the margin/contingency residual, and `Total Contract Amt`
   repeats per resource row. Separate `DirectTieOutDelta` from `ContractUplift`; dedup contract/indirect
   totals per BOQ item.
3. Class-2 cohorts dimensionally invalid: norms mix work units; rates mix resource descriptions and
   consumption units. Fix cohorts to comparable unit / resource-description / consumption-unit groups with
   a minimum sample gate.
4. Strict LOO + same sub-trade ⇒ zero Class-3 peers (each of 68 sub-trades maps to one package). Add
   prior-project peers or ship Class 3 explicitly fully suppressed.
5. Class 3 undefined on cumulative periods/completion: summing panel rows double-counts cumulative AC.
   Select each BCC's latest eligible completed row, then aggregate matching resource AC over earned qty.
6. Singleton Tower-X workbook attached to every project, plan permitted rendering for mismatched projects.
   Bind the estimate source to project id/slug; return unavailable for non-Tower-X projects.

#### Round-1 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Wrong quantity formula / missing fields | Added the **verified workbook schema** section; `ResourceLine` now carries `QtyPerUnitWork`, `ConsumptionUnit`, `IndirectCost`, `TotalResourceQty`; removed non-existent gang/equipment counts; **G1** now reconciles a recomputed `Total Resource Qty` to the stored column (tie-out is the validator) and includes indirect cost. |
| 2 | Tie-out vs uplift conflation; contract-amt repetition | **G2**: `DirectTieOutDelta` (≈0 proof) and `ContractUplift` (=margin+cont) are distinct; `Total Contract Amt` + `Indirect Cost` deduplicated per BOQ item before rollup. |
| 3 | Invalid Class-2 cohorts | **G10**: Output-Norm percentile within (sub-trade+unit); Unit-Rate band within (resource type+description+consumption unit); min-sample gate suppresses thin cohorts. |
| 4 | Zero Class-3 peers under same-sub-trade LOO | **G5**: peer key relaxed to poolable (unit+resource type+procurement route), sub-trade advisory only; explicit `Class3FullySuppressed` + honest UI when the workbook yields <5 peers everywhere. |
| 5 | Cumulative double-count / completion | **G6**: latest eligible completed row per BCC, resource-type AC ÷ earned qty; non-completed centres excluded. |
| 6 | Workbook bound to every project | **G8**: `TryLoadFor(projectSlug)` returns the model only for `Data:EstimateProjectSlug` (tower-x); other projects → null/unavailable; mismatched rendering removed. |

### Round 2 (2026-07-07) — blocking findings

1. Quantity/tie-out math still wrong: all resource types compute qty as `BOQ Qty × Qty/Unit Work ÷ Output
   Norm`; sum per-line indirect costs (they are **not** repeated); define `DirectTieOutDelta =
   Σ(ResourceCost + IndirectCost) − BOQ Direct+Indirect Amount`; deduplicate **only** `Total Contract Amt`.
2. Class-2 minimum sample unspecified ("e.g. 5"). Fix a deterministic constant `MinCohortN = 5` with
   boundary tests at 4 and 5.
3. "Completion gate" undefined. Fix eligibility to `ActualPctComplete >= 100` (optionally `AlertLevel ==
   CLOSED`), then select max `PeriodId` per BCC.
4. Registry still receives only `projectId`; no concrete slug path. Bind the estimate source by owning
   project id (resolve the slug once at startup) so `Build` gates on `projectId` — or thread `projectSlug`.

#### Round-2 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Formula / indirect / dedup | **Verified against the workbook** (openpyxl): TRQ = `BOQ Qty × Qty/Unit Work ÷ Output Norm` for *all* types; `Indirect Cost` is per-line (summed), `Total Contract Amt` repeats per item (deduped). **G1/G2** rewritten to this; `DirectTieOutDelta = Σ(Resource Cost + Indirect Cost) − BOQ Direct+Indirect Amount`. |
| 2 | Unspecified min sample | **G10**: `MinCohortN = 5` and `MinPeerN = 5` are named constants (config-overridable) with boundary tests at 4/5. |
| 3 | Undefined completion gate | **G6**: eligibility = `ActualPctComplete >= 100` (or `AlertLevel == CLOSED`), then max `PeriodId` per BCC. |
| 4 | No slug/id path to registry | **G8**: `TryLoadForProject(long projectId)` gated on the once-resolved owning project id; `Build` already has `projectId`, so no `GetOrBuild/Rebuild` signature change. |

### Round 3 (2026-07-07) — blocking findings

1. `MinCohortN`/`MinPeerN` were "config-overridable" — fix as non-overridable constants = 5.
2. G6 still admitted `AlertLevel == CLOSED` without 100% completion — require only `ActualPctComplete >= 100`.
3. Class 3 needs unit matching, but `CostCentrePeriod`/Postgres loader omit BCC `unit`.
4. `ProjectDirectory` cannot resolve a slug at startup without user context — use a bypass-capable resolver, fail closed.
5. Class-2 rules undefined ("top percentile"/"bottom of band"/"thin contingency") — specify constants, quantile method, tie handling.

#### Round-3 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Config-overridable constants | **G10**: `MinCohortN`/`MinPeerN` are non-overridable `const int = 5`. |
| 2 | CLOSED without 100% | **G6**: eligibility is **only** `ActualPctComplete >= 100`; CLOSED alternative removed. |
| 3 | Panel lacks BCC unit | **G11 (new)**: Class-3 peer attributes (unit/resource type/procurement) sourced from the `EstimateModel`, not the panel; panel gives realized AC + earned qty only. **No loader/schema change** (decision 1). Multi-unit packages excluded. |
| 4 | Startup slug resolution | **G8**: owning id resolved once via the existing bypass-role `ProjectResolver.ResolveAsync` (verified in `Web.API/Tenancy/ProjectResolver.cs` — `SET ROLE qs_bypass`, cached); fail closed to null. |
| 5 | Undefined Class-2 rules | **G12 (new)**: `RulesVersion="v1"`; quantiles via type-7 linear interpolation, ties by `(NormCode,Sec,ItemRef,ResourceType)`; `OutputNorm ≥P90`, `UnitRate ≤P10`, `Cont% <2%` / `==0`. |

### Round 4 (2026-07-07) — blocking findings

1. Class-3 math undefined — specify `EstimatedUnitCost`, peer aggregation, `PeerBandLow/High` quantiles,
   `DeltaPct`, and finite/positive gates; compare like-for-like resource cost incl. per-line indirect.
2. Class-3 route/unit attribution not guaranteed — single-unit exclusion misses multi-route packages and
   doesn't prove earned-qty units. Join each completed BCC's `WbsCode` to a unique estimate item, derive
   unit/route there, exclude unmatched/ambiguous.
3. Class-2 tie semantics contradictory — type-7 gives value thresholds but identifier tie-break could flag
   equal values differently. Use `value >= P90` / `value <= P10`, equal values treated equally; make
   contingency rules mutually exclusive (`==0` zero, `0<v<2` thin).

#### Round-4 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Undefined Class-3 math | **G13 (new)**: exact `EstimatedUnitCost` (incl. per-line indirect) / `RealizedUnitCost`, P25/P50/P75 peer band (type-7), `DeltaPct`, finite/positive drop gates. |
| 2 | Attribution not identifiable | **G11 rewritten**: verified `WBS_Code` = BOQ `Item` ref (BCC↔item 1:1); attribute unit/route per BCC via `WbsCode`→item, not per package (21/68 packages are multi-unit); exclude unmatched/ambiguous. |
| 3 | Contradictory tie semantics | **G12 rewritten**: flag by value comparison (`≥P90` / `≤P10`), equal values identical (no identifier tie-break for inclusion); `ZeroContingency` (`==0`) and `ThinContingency` (`0<v<2`) mutually exclusive. |

### Round 5 (2026-07-07) — blocking findings

1. Class-3 peer granularity contradictory: G6 aggregated at package level while G13 treated each BCC/item
   as a peer, letting one package inflate `PeerCount` and dominate quantiles. Group eligible BCCs by
   `(PackageCode, unit, resourceType, procurementRoute)`, compute `ΣAC_rtype / ΣEarnedQty` per package,
   count **distinct peer packages**, and take P25/P50/P75 over package-level observations.

#### Round-5 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Peer-granularity contradiction | **G13 + Class 3 rewritten** to the **package-cell** grain `(PackageCode, unit, resource type, procurement route)`: one observation per package (`ΣAC_rtype / ΣEarnedQty`), `PeerCount` = distinct peer packages, P25/P50/P75 over package-level observations. G6 (BCC eligibility) now feeds the per-cell aggregation rather than being a separate grain. Verification asserts a duplicated-BCC fixture cannot inflate the count or move the median. |

### Round 6 (2026-07-07) — blocking findings

1. `EstimatedUnitCost(cell)` omitted `procurementRoute` filtering — filter items by both cell unit and route.
2. Eligibility applied `ActualPctComplete >= 100` before taking the latest row — select the latest row
   first, then check completion, so stale earlier 100% rows can't qualify.
3. Dropping zero-AC BCCs also drops their earned quantity, biasing unit cost upward — keep valid zero-AC
   BCCs (or suppress the cell if zero means missing).

#### Round-6 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Estimated cost missed route filter | **G13**: `EstimatedUnitCost(cell)` now sums cost and quantity over items matching the cell's **unit + procurement route**. |
| 2 | Completion-order bias | **G6**: take each BCC's **latest** row first, **then** require that row's `ActualPctComplete >= 100`. |
| 3 | Denominator bias from dropped zero-AC | **G13**: keep finite `AC_<rtype> ≥ 0` with `Earned_Qty_Cumul > 0` (zero AC retained in the denominator); drop a cell only if aggregate `ΣAC ≤ 0` or `ΣEarnedQty ≤ 0`. |

### Round 7 (2026-07-07) — blocking findings

1. Class 3 could use the target package's actuals to create/qualify its cell (contradicts G4) — enumerate
   target cells from estimate data only; build the realized peer pool separately, then exclude the target.
2. `Class3FullySuppressed` doesn't imply "0 peers" (cells may have 1–4) — replace UI text with "No cell
   meets the 5-peer minimum" and show actual peer counts.
3. Round-6 regressions untested — add fixtures for latest-row completion, zero-AC denominator retention,
   and route-filtered estimated cost.
4. Class-1 PASS/FAIL tolerances undefined — specify decimal absolute/relative tolerances with boundary tests.

#### Round-7 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Target-cell leakage | **G4 rewritten**: target cells enumerated from estimate only; realized peer pool built separately then target package LOO-excluded; end-to-end leakage test. |
| 2 | Misleading suppression copy | Report field renamed `Class3NoCellMeetsMinPeers`; UI shows actual 1–4 peer counts and "No cell meets the 5-peer minimum" (not "0 peers"). |
| 3 | Untested regressions | **Verification** adds the three explicit fixtures (latest-row 80% exclusion; zero-AC denominator retention; route-filtered numerator+denominator). |
| 4 | Undefined Class-1 tolerances | **G0 (new)**: named tolerance constants (quantity rel 1e-6; money 0.01 AED/item; rollup 1 AED); `TiesOut` defined against them; boundary tests. |

### Round 8 (2026-07-07) — blocking findings

1. Class-1 could report `TiesOut=true` despite failed quantity/resource-cost checks. Define PASS as all
   line quantity + cost identities, all per-item deltas, and the rollup within 1 AED; also fail on
   inconsistent repeated `TotalContractAmt`; add a regression test per case.

#### Round-8 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | `TiesOut` could mask failures | **G0 rewritten**: `TiesOut = true` iff **all** of (a) line quantity re-derivation, (b) line `Resource Cost` identity, (c) per-item `DirectTieOutDelta`+`ContractUplift`, (d) rollup ≤ 1 AED, and (e) **consistent** repeated `Total Contract Amt` hold; any single failure flips it false. Verification adds one regression fixture per failing case (incl. inconsistent repeated contract amount). |

### Round 9 (2026-07-07) — blocking findings

1. `ReconciliationResult` lacked per-check booleans (`ResourceCostIdentityOk`, `RepeatedContractAmtConsistent`)
   and the UI listed only non-zero `DirectTieOutDelta`, so a line-identity or repeated-value failure could
   show an unexplained FAIL. Add per-check booleans/details to DTOs and render every failed conjunct.
2. "Project rollup ≤ 1 AED" was undefined. Specify and test the exact project-level deltas
   (direct-cost reconciliation; contract-uplift vs summed margin+contingency), each at the 1 AED tolerance.

#### Round-9 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Incomplete FAIL diagnostics | `ReconciliationResult` now carries `QuantityReDerivationOk`, `ResourceCostIdentityOk`, `RepeatedContractAmtConsistent`, `DirectTieOutOk`, `ContractUpliftOk`; the Class-1 banner renders **every** failed conjunct with offending items/deltas (no bare FAIL). |
| 2 | Undefined project rollup | **G0**: two named project deltas — `ProjectDirectDelta = Σ(Resource+Indirect) − Σ BOQ Direct+Indirect` and `ProjectUpliftDelta = Σ ContractUplift − Σ(Margin+Cont)` — each within 1 AED, on the rollup and tested. |

### Round 10 (2026-07-07) — blocking findings

1. `ContractUpliftOk` ambiguous — `ContractUplift` is legitimately non-zero, so add `ContractUpliftDelta =
   ContractUplift − (MarginAmount + ContingencyAmount)` and test that delta.
2. FAIL diagnostics lack line-level deltas — add offending-line/item detail (actual, expected, delta,
   tolerance) for quantity, resource-cost identity, and repeated contract amounts, exposed via DTOs.

#### Round-10 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Uplift check ambiguous | **G0** + `ReconciliationResult.ContractUpliftDelta` (= uplift − (margin+cont)); the **delta** is the tested value, not the raw non-zero uplift. |
| 2 | No line-level FAIL detail | `ReconciliationResult.Failures: [ReconciliationFailure { Scope, Check, Line?, Actual, Expected, Delta, Tolerance }]`, exposed via DTO and rendered per failed conjunct in the banner. |

### Round 11 (2026-07-07) — blocking findings

1. **Circular project dependency** — `EstimateModel`/`IEstimateSource` were placed in Core while the loader
   lives in Infrastructure; Infrastructure would have to reference Core, which already references
   Infrastructure. Move shared estimate records + interface to Domain (or the loader to Web.API).
2. **Class-3 unit attribution absent from the item model** — G11 needs the BOQ-item `Unit` but `BoqLine`/
   `BoqMapping` omitted it. Add `Unit` and load from sheet 1/3; use it for cell attribution.

#### Round-11 reconciliation

| # | Finding | Resolution in this plan |
|---|---------|-------------------------|
| 1 | Circular dependency | Verified layering (`Core→Domain+Infra`, `Infra→Domain`); moved `EstimateModel` records + `IEstimateSource` to **`QsEarlyWarning.Domain/Estimate/`** (loader in Infra→Domain, engine+result models in Core→Domain). New "Estimate input model + interface" section documents it. |
| 2 | Missing item `Unit` | Added `Unit` to `BoqLine` and `BoqMapping`; **authoritative item unit = `BoqMapping.Unit`** (sheet 3), cross-checked against `BoqLine.Unit`/`ResourceLine.Unit`, mismatch excludes the item (G11). |

### Round 12 (2026-07-07) — **CLEAN**

> **"no remaining blocking findings."** Codex confirmed the dependency and unit-attribution fixes and found
> no further correctness/leakage/honesty/build defects. It raised one **non-blocking** caveat — Excel
> percentage cells could load as fractions — which was **verified against the raw workbook**: `Margin %`/
> `Cont %` are stored as percentage *points* (`22`, `8`), so the G12 `Cont % < 2` threshold is correct with
> no rescale (recorded in the verified-schema section).

**Review loop closed clean at round 12.** Preserve during implementation: the three separated output
classes, the Output-Norm-divisor tie-out, the estimate-side/zero-actual boundary for Class 2, the Class-3
leave-one-out + package-cell grain + 5-peer suppression, and the Domain-hosted estimate model.
