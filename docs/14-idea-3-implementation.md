# Idea 3 — Estimate Assumption Stress Test: How It Was Actually Built

> Companion to the product-facing [Feature 3 — Estimate Assumption Stress Test](03-estimate-stress-test.md)
> and the original spec [`ideas/idea-3-should-cost-auditor.md`](../ideas/idea-3-should-cost-auditor.md).
> This doc is the **engineering reality**: what shipped, where the code lives, the math, and how a QS
> (or a developer) actually uses it. It mirrors the deep dives for
> [Idea 1](12-idea-1-implementation.md) and [Idea 2](13-idea-2-implementation.md).

## From spec to shipped — the short version

The [idea spec](../ideas/idea-3-should-cost-auditor.md) was reframed twice by review before it was
built. It started as a "bottom-up should-cost auditor" that would flag under-priced packages, and both
the CEO-mode review and Codex killed that framing as a **tautology**: because the workbook reconciles
end-to-end, rebuilt should-cost *equals* BOQ direct cost by construction (so the comparison only
re-derives `Margin %` + `Cont %`), and "flag under-priced, watch it overrun" is `CPI < 1` **by
identity** (estimate-vs-own-actual unit cost *is* `EV/AC`). The surviving product is an **Estimate
Assumption Stress Test**: a deterministic engine that emits **three explicitly separated output
classes**, never fused into one score, because they carry very different evidentiary weight.

What shipped follows that framing faithfully, with two deliberate departures:

| Spec said | What shipped | Why |
|-----------|--------------|-----|
| A batch **Python** engine (`assumption_stress_test.py`) + a Streamlit/HTML heatmap + a pytest reconciliation suite | **C# deterministic engine in `QsEarlyWarning.Core.StressTest`** + three read-only API endpoints + a React "Stress Test" tab; the reconciliation suite is `StressTestTests` (xUnit) | The rest of the product is .NET; one runtime. The credibility artifact — should-cost ties to the AED — is an assertion in the test suite, run on every build. |
| Class 2 includes "risky norm `Notes` adjustments (e.g. −30% confined spaces)" as a flag | **`Notes`-derived flags dropped**; Class 2 ships **three** cohort-gated checks only: aggressive Output Norm, thin Unit Rate, thin/zero Contingency | The spec itself said `Notes` rules are review prompts *only if their logic is deterministic and versioned*. The parsing isn't, so shipping it would be a non-reproducible flag — cut rather than fake. |

Everything else — the three separated classes, the Output-Norm-divisor quantity math, the strictly
estimate-side Class 2, the leave-one-out + 5-peer-minimum + retrospective-only Class 3, published peer
counts, the rules version on every flag, and the "review prompts, not verdicts" framing — is
implemented as specified.

## The three output classes (never fused into one score)

| Class | What it is | Evidentiary weight | Reads actuals? |
|-------|-----------|--------------------|----------------|
| **1 — Reconciliation tie-out** | Rebuild should-cost from norms × rates; confirm it ties to the BOQ direct cost + contract uplift end to end | A **correctness PROOF of the engine's arithmetic** — *not a signal* | No |
| **2 — Assumption flags** | Cohort-gated, exact-threshold review prompts: aggressive Output Norm, thin Unit Rate, thin/zero Contingency | Day-zero **review prompts**, not verdicts | **No** — enforced by a test |
| **3 — Peer benchmark** | Benchmark a package-cell's estimated unit cost against *other* completed packages' realized unit cost | **Retrospective validation only** — same-project peers don't exist at award | Yes (gated, leave-one-out) |

The whole point of the separation is that Class 1 must never be read as a signal, and Class 3 must
never be presented as an at-award flag on this single-project data.

## The pipeline, end to end

```
Workbook sheets 1-4 → EstimateModel (joined estimate graph)      [built once, project-gated]
   │
   ├─ EstimateWorkbookLoader   project-gated, memoizing; fails CLOSED (wrong project / bad file → null)
   ├─ EstimateStressTester.Run  the deterministic engine — pure, byte-identical on re-run
   │      ├─ Reconcile   (Class 1)  per-BOQ-item conjunct checks + project rollup → ReconciliationSummary
   │      ├─ Class2      (Class 2)  cohort-gated Output-Norm / Unit-Rate / Contingency flags + package heat
   │      └─ Class3      (Class 3)  package-cell peer benchmark, LOO, ≥5 peers, needs completed centres
   │            │  (panel = 9_HISTORICAL_DATA actuals; null on projects with no panel)
   └─ StressTestReport  hung on the per-tenant ProjectSnapshot (null unless this is the estimate's owner)
        │
        ├─ StressTestController   GET /api/v1/stress-test/{reconciliation,assumptions,peer-benchmark}
        ├─ QsAnalyticsTools       stress_flags_for_package tool for the QS Copilot
        └─ StressTest.tsx         the "Stress Test" tab (three classes visibly separated)
```

The report is **built once when the project snapshot is built** (`ProjectSnapshotRegistry`), only for
the project that owns the estimate workbook, and degrades gracefully to `null` (a fit/parse failure
never sinks the snapshot — the watchlist, forecast, and EVM still work).

## The estimate graph — `EstimateModel`

`EstimateWorkbookLoader` (a thin, **project-gated, memoizing** `IEstimateSource`) delegates parsing to
`EstimateWorkbookReader`, which reads the four estimate sheets into one immutable joined graph
(`Domain/Estimate/EstimateModel.cs`):

- `EstimateNorm` ← `2_ESTIMATE_NORMS` (`OutputNorm`, sub-trade, unit, `Notes`, …)
- `BoqLine` ← `1_BOQ` (`Quantity`, `DirectIndirectAmount`, `MarginAmount`, `ContPct`, `ContingencyAmount`, …)
- `BoqMapping` ← `3_BOQ_MAPPING` (authoritative `Unit`, `EstimatePackage`, `Procurement`, `NormCode`)
- `ResourceLine` ← `4_ESTIMATE_DATASHEET` (`UnitRate`, `ResourceCost`, `IndirectCost`, `TotalResourceQty`, …)

It exposes the lookups the engine needs: `NormByCode`, `BoqByItemRef`, `MappingByItemRef`,
`ResourceLinesByItemRef`. **Amounts are AED; percentages are percentage points** (e.g. `Cont %` = 8, not
0.08); missing/sentinel cells parse to `null`, never a value. The model lives in `Domain` so both the
Infrastructure loader and the Core engine can reference it without a circular dependency.

The loader **fails closed**: it is bound to one owning project id at startup, and `TryLoadForProject`
returns the memoized model only for that id — any other project, or a missing/invalid workbook, returns
`null` (the stress test is simply unavailable there).

## The math, class by class

Everything is in `QsEarlyWarning.Core.StressTest.EstimateStressTester` — pure and deterministic, so the
same workbook yields a byte-identical report. All thresholds are **frozen constants** (`RulesVersion =
"v1"`), stamped onto every flag.

### Class 1 — arithmetic reconciliation tie-out (the correctness proof)

For each BOQ item that has resource lines, five conjuncts are each their own boolean; `TiesOut` is their
AND:

```
(a) QuantityReDerivation   Total Resource Qty  ==  BOQ Qty × Qty-per-Unit-Work ÷ Output Norm
(b) ResourceCostIdentity   Resource Cost       ==  Total Resource Qty × Unit Rate
(c) DirectTieOut           Σ(ResourceCost + IndirectCost)  ==  BOQ Direct+Indirect Amount
(d) ContractUplift         (Total Contract Amt − direct−indirect)  ==  Margin + Contingency
(e) RepeatedContractAmt    the item's repeated Total Contract Amt values agree (dedup → one)
```

**Check (a) is the load-bearing one** — it is the exact correction from `data/README.md`: the
**Output-Norm divisor** (`÷ Output Norm`) applies **uniformly across all resource types** in this
workbook. Drop it and labour/equipment quantities are overstated and everything falsely flags. A
dedicated test (`Class1_output_norm_divisor_is_load_bearing`) recomputes a manpower line **with and
without** the divisor and confirms only the divided form matches the stored quantity.

**Tolerances (frozen):** quantity relative `1e-6`, per-item money absolute `0.01` AED, project-rollup
absolute `1.0` AED. Every failed conjunct emits a `ReconciliationFailure` carrying `(actual, expected,
delta, tolerance)`, so a FAIL is **always explained, never bare**. The project rollup ties out only if
**every** item ties out *and* both project deltas ≤ 1 AED. Because the model reconciles by construction,
the residual `Contract − (direct+indirect)` is **exactly** `Margin + Contingency` — which is precisely
why this is a proof, not a signal.

### Class 2 — estimate-side assumption flags (reads zero actuals)

Three cohort-gated checks, each producing a review prompt with an **exact threshold** and its cohort
size. A cohort is skipped entirely below `MinCohortN = 5` (no flagging on a thin cohort):

| Flag `Kind` | Cohort | Rule | Severity |
|-------------|--------|------|----------|
| `OutputNormTopPercentile` | `(sub-trade code + unit)` | `OutputNorm ≥ P90` of the cohort (aggressive productivity assumption) | medium |
| `UnitRateBottomOfBand` | `(resource type + description + consumption unit)` | `UnitRate ≤ P10` of the cohort (thin rate) | medium |
| `ZeroContingency` / `ThinContingency` | all BOQ items | `Cont % == 0` (high) or `0 < Cont % < 2pp` (medium) — **mutually exclusive** | high / medium |

Quantiles are **Type-7 (linear-interpolation)** (`Quantile`, unit-tested against known values). An
Output-Norm flag fans out to **every package that uses that norm**, and each flag carries its **source
BOQ item refs** (the package's items using the norm) so a consumer can cite the exact rows. The flags
roll up into a **package heat cell** (`(package, discipline)` → flag count, high count, severity),
ordered by high-count then flag-count.

**Class 2 reads no actuals — and it's enforced.** `Class2_reads_no_actuals_identical_with_or_without_
panel` runs the engine with and without the actuals panel and asserts the flags are *identical*. The
whole class is also asserted **deterministic** across re-runs, and every flag is asserted to carry
`RulesVersion == "v1"` and `CohortN ≥ 5`.

### Class 3 — retrospective peer benchmark (leave-one-out, gated, never at-award)

The differentiated-but-honest class. It benchmarks each estimate **package-cell** against *other*
packages' **realized** unit cost — never its own — at the grain
`Cell = (Package, Unit, ResourceType, ProcurementRoute)`:

1. **Estimate cell unit cost** = `Σ(ResourceCost + IndirectCost) ÷ Σ(distinct item BOQ quantity)`.
2. **Realized cell unit cost** — from `9_HISTORICAL_DATA`: take the **latest row per BCC**, keep only
   **completed** centres (`Actual % ≥ 100` with a positive earned quantity), split AC by the four
   resource types (`AcManpower/Material/Equipment/Subcontract`), and divide by cumulative earned
   quantity → one realized observation per package-cell.
3. **Peers** = other packages' realized cells with the **same `(unit, resource type, route)`**
   (**leave-one-out on package** — a package can never score itself).
4. If `peers ≥ MinPeerN = 5`: report `PeerMedian`, the `P25–P75` band, `PeerCount`, and
   `DeltaPct = (est − median) ÷ median`, status **`Benchmarked`**. Otherwise status **`Suppressed`**,
   still publishing the **real** peer count (1–4), **never a false "0"**.

**Leakage guards (hard):** items whose unit is *ambiguous across sheets* are excluded and counted;
completed centres not matching a single estimate item are excluded and counted. On this single-project
workbook **no centre is complete** (median ~13% progress), so **no cell meets the 5-peer minimum** —
`Class3NoCellMeetsMinPeers` is `true`, and the notes say so explicitly. That is the honest outcome the
spec demanded: a genuine day-zero benchmark would need completed **prior-project** peers, which this data
doesn't have.

## Validating the results — `dotnet test` (`StressTestTests`)

The engine's credibility is pinned by an automated suite (`tests/QsEarlyWarning.Tests/StressTestTests.cs`):

| Test | What it locks down |
|------|--------------------|
| `Loader_reads_all_four_estimate_sheets` | the joined graph loads (norms/BOQ/mappings > 100, resource lines > 500) |
| `Loader_is_project_gated` | loads only for the owning project id; wrong id / null id → `null` (fail closed) |
| `Class1_reconciliation_ties_out_to_the_AED` | **the credibility artifact** — should-cost ties out across all BOQ items; project uplift delta ≤ 1 AED |
| `Class1_output_norm_divisor_is_load_bearing` | only the `÷ Output Norm` form matches the stored quantity; the dropped-divisor bug would overstate it |
| `Class2_reads_no_actuals_identical_with_or_without_panel` | Class 2 is strictly estimate-side (identical flags with/without the actuals panel) |
| `Class2_is_deterministic_and_flags_carry_a_rules_version` | byte-identical re-runs; every flag stamped `v1` and `CohortN ≥ 5` |
| `Class2_contingency_rules_are_mutually_exclusive` | no item is both zero- and thin-contingency |
| `Class3_never_uses_a_packages_own_actual_leave_one_out` | every benchmarked cell has `PeerCount ≥ 5` from *other* packages |
| `Class3_suppressed_below_five_peers_and_reports_actual_counts` | 1–4-peer cells are Suppressed but publish their real count; the no-min flag means "no cell meets the minimum", not "0 peers" |
| `Class3_absent_panel_yields_no_benchmarks_and_the_no_min_flag` | no panel → no benchmarks + the honest no-min flag |
| `Quantile_type7_matches_known_values` | the Type-7 quantile is correct (P50/P0/P100/P90 on `{1..5}`) |

> The reconciliation tie-out is the engine's correctness proof, not its signal — exactly as the spec
> insisted. All tests pass on the Tower X workbook.

## How users make use of it

Three entry points, all reading the **same computed report** off the project snapshot — no drift.

### A. The Stress Test tab (primary — for the QS/estimator)

Open the app → **Stress Test** tab (`StressTest.tsx`). It renders the three classes **visibly
separated**:

- **Class 1** — a green "✓ ties out to the AED across all N items; residual is exactly margin +
  contingency — a **correctness proof**, not a signal" banner, with KPI tiles (items reconciled,
  contract total, margin, contingency). On a FAIL it shows the failing conjuncts with actual/expected/Δ.
- **Class 2** — a **package heat map** (severity-coloured cells) plus a filterable flag table (flag
  kind, package, discipline, driving line, exact reason), labelled "review prompts, cohort-gated (≥5),
  rules v1".
- **Class 3** — a **RETROSPECTIVE** pill and the explicit caveat that same-project peers don't exist at
  award; the table shows each cell's estimated unit cost, peer median, **actual peer count**, and
  Benchmarked/Suppressed status. On this workbook it states no cell meets the 5-peer minimum.

When the project has no estimate workbook, the tab shows a clean empty state (the stress test runs only
on the estimate's owning project, Tower X).

**Workflow:** run once at award. Read Class 1 as the arithmetic sanity check, work the Class 2 heat map
top-down (re-price, add contingency, or challenge the norm while it's still free to fix), and treat
Class 3 as retrospective research only.

### B. The API (for integration / scripting)

```
GET /api/v1/stress-test/reconciliation                     # Class 1 tie-out
GET /api/v1/stress-test/assumptions?discipline={value}     # Class 2 flags + heat (discipline optional)
GET /api/v1/stress-test/peer-benchmark                     # Class 3 retrospective benchmark
Headers: X-User-Id, X-Project-Slug                         # authenticated identity + selected project
```

Every request is authorized against project membership first (`StressTestController.Resolve`). A project
with no estimate workbook returns **`available: false`** with a clean note (not an error). Errors: `401`
no identity · `403` not a member · `404` no data. Non-finite doubles are sanitized to `null`.

### C. The QS Copilot (plain English)

The report is wired into the copilot as the **`stress_flags_for_package`** tool
(`Core/Agent/QsAnalyticsTools.cs`): ask *"any estimate red flags on EP-STR-CON?"* and it returns that
package's Class-1 tie-out status and Class-2 flags (kind, severity, reason, source item refs), stamped
with the rules version — the **exact same** computed report the tab shows, described as review prompts,
not verdicts. (Class 3 is deliberately not surfaced as a per-package tool, keeping the retrospective
benchmark out of a day-zero answer.)

## Honest limits

- **Not an under-pricing oracle.** With one project it flags assumptions *for review*; it never proves
  anything is objectively under-priced. Class 2 is prompts, not verdicts.
- **Class 1 is a proof, not a signal.** The estimate reconciles by construction — the tie-out validates
  arithmetic (and the Output-Norm divisor), nothing more.
- **Class 3 is retrospective only.** Same-project peer actuals don't exist at award; on this workbook no
  cell even meets the 5-peer minimum (no completed centres). The day-zero product is Classes 1 + 2.
- **`Notes`-derived flags cut.** The spec's "risky notes adjustment" flag is not shipped — its logic
  isn't deterministic/versioned, so flagging on it would be non-reproducible.
- **Single project, one owner.** The whole feature runs only on the estimate's owning project.

## Where to look in the code

| Concern | File |
|---------|------|
| The deterministic engine (all 3 classes, quantiles, thresholds) | `Core/StressTest/EstimateStressTester.cs` |
| Result records (reconciliation, flags, heat, benchmarks, report) | `Core/StressTest/StressTestModels.cs` |
| Joined estimate graph + lookups | `Domain/Estimate/EstimateModel.cs`, `Domain/Estimate/IEstimateSource.cs` |
| Project-gated, memoizing loader (fail-closed) | `Infrastructure/Excel/EstimateWorkbookLoader.cs`, `.../EstimateWorkbookReader.cs` |
| Snapshot wiring (build once, degrade gracefully) | `Core/Registry/ProjectSnapshotRegistry.cs` |
| HTTP endpoints (auth + `available:false`) | `Web.API/Controllers/StressTestController.cs`, `Web.API/Contracts/StressTestDtos.cs` |
| Copilot tool | `Core/Agent/QsAnalyticsTools.cs` (`StressFlagsForPackage`) |
| UI | `frontend/qs-early-warning/src/components/StressTest.tsx` |
| Tests | `tests/QsEarlyWarning.Tests/StressTestTests.cs` |
</content>
</invoke>
