# Plan — Integrate Idea 4 (QS Copilot: conversational agent over the workbook)

## Context

Idea 4 (`ideas/idea-4-qs-copilot.md`) is a Claude-powered agent that **opens on a proactive drift
watchlist** and then answers free-form QS questions ("which packages are drifting and why?", "next-period
spend forecast for BCC-X", "project CPI in period 9") — the LLM is an **orchestrator over deterministic,
tested tools**, never the calculator. Its contribution is the **interface + traceability layer** over the
platform's already-validated detection (idea-1 watchlist), forecast (idea-2), and estimate stress test
(idea-3): every AED number arrives in plain English **with the sheet + rows behind it**.

**A working copilot already exists** (`QsEarlyWarning.Agent`, Microsoft Agent Framework over Anthropic;
`POST /api/v1/copilot/ask`; React chat UI; 4 read-only tools — `GetWatchlist`, `GetCostCentreDetail`,
`ExplainDrift`, `GetEvmSnapshot`; tool-scope tests). This plan **extends** it — reuse over rebuild.

**Decisions (confirmed via AskUserQuestion):**
1. **Migrate the copilot tools to the per-project Postgres snapshot** (tenant-scoped, RLS). Today the tools
   read a startup Excel singleton (`IModelProvider`), diverging from the dashboard/forecast/stress-test
   which read the per-project `ProjectSnapshot`. After migration the copilot reads the **same snapshot**,
   so it is multi-project, RLS-enforced (a non-member is rejected *before* any LLM call), and can expose
   the forecaster + stress test that already hang on the snapshot.
2. **Full idea-4 scope** — add `forecast_incremental_spend` (validated) + `directional_eac` (flagged
   unvalidated) + `resource_split` + `project_evm` (aggregated CPI `sum(EV)/sum(AC)`, SPI `sum(EV)/sum(PV)`)
   + a stress-flags tool; a **proactive watchlist opener** in the UI; and **resolved-filter +
   excluded-row-count + source-row IDs** on every answer. (Raw `query_boq` is dropped — see G9.)
3. **Deterministic ground-truth eval harness (CI-safe) + opt-in live-LLM runner** (gated on an API key).

**Save location:** this doc at `plan/idea-4-qs-copilot-integration-plan.md`.

## Correctness & trust guards (non-negotiable — front-loaded)

- **G1 — tools compute, model narrates.** The model performs **no arithmetic**. Every number is read from
  a pre-computed panel column or computed in tested tool code and returned **pre-formatted**. Enforced by
  the code boundary (read-only typed tools), not a prompt. A regression test asserts the tool surface.
- **G2 — aggregated ratios use the correct summed denominators, never the mean of per-row ratios.**
  Aggregated **CPI = `sum(EV)/sum(AC)`** and aggregated **SPI = `sum(EV)/sum(PV)`** (distinct denominators
  — SPI is *not* `sum(EV)/sum(AC)`) over the rows in scope; per-row CPI/SPI is only ever reported per row.
  **Eligibility is ratio-specific and keys on the denominator only** (G4): a row is in the CPI sum iff its
  AC is finite and `> 0` (EV may be zero — a zero-EV/positive-AC row is a real bad signal and *must* count,
  dragging CPI down); a row is in the SPI sum iff its PV is finite and `> 0`. This is the headline
  silent-error trap; deterministic tests compute ground truth both ways and assert the tool uses the
  aggregated form for **each** ratio (and that each differs from mean-of-rows on this data).
- **G3 — validated forecast vs directional EAC boundary (no bypass).** `forecast_incremental_spend`
  returns idea-2's validated next-period band (horizon + P10/P50/P90 + trust). `directional_eac` returns
  `BAC/CPI` (the workbook formula) **flagged `validated:false`** with a note that it is an unvalidated
  extrapolation, and is the **sole** source of any final-cost figure. To close the bypass, the existing
  `GetEvmSnapshot` tool is **stripped of its EAC and VAC fields** (VAC = BAC − EAC is equally
  final-cost-derived) — it returns only the validated per-period identities CV/CPI/SPI; `directional_eac`
  returns EAC **and** VAC, both flagged. The copilot can therefore never surface a final-cost number
  without the unvalidated flag; the system prompt + tool contracts say so, and an eval case covers it.
- **G4 — exclude only zero/missing-denominator rows, and report the count per ratio.** For an aggregate,
  a row is excluded **only** when its denominator is missing/non-finite or `≤ 0` (CPI: AC; SPI: PV) — this
  drops NOT STARTED rows (zero AC/PV) but **keeps** zero-numerator rows that carry a real signal. Zero rows
  are never silently folded: the tool returns the **included and excluded row counts per ratio** in its
  provenance, so a skewed aggregate can't hide. (The watchlist scorer keeps its own `IsScoreableGreen`
  eligibility for ranking — a separate concern from aggregation.)
- **G5 — arguments validated against the real key set.** Unknown BCC id, out-of-range period, or unknown
  package returns a **typed tool error** the model surfaces as a clarification — never a guessed answer.
- **G6 — RLS tenant boundary before the LLM.** The controller resolves the snapshot via the tenant
  `Resolve` pattern (401/403/404) *before* invoking the agent, so a non-member never reaches a tool call.
- **G7 — every answer echoes its provenance, cited by the row's natural key.** Resolved period,
  BCC/package filter, aggregation method, excluded-row count, **source row IDs**, and the tool used are
  returned in structured evidence and shown in the UI "sources" panel. **Row IDs are the row's natural
  composite key** — `"{BccId}@P{PeriodId}"` for a sheet-9 panel row (the loader enforces `(BccId, PeriodId)`
  uniqueness, so this deterministically and verifiably locates the exact row) and the BOQ item ref for an
  estimate-derived row. This needs **no** surrogate id, Excel row number, or loader/schema change; the eval
  checks the cited keys against actual rows (G8). A wrong-period/wrong-grain answer is caught in the citation.
- **G8 — eval leakage guard.** Ground-truth answers and cited row IDs are computed **independently** from
  the panel/estimate in the test, then compared to the tool output — never scored against the model's own
  claim.
- **G9 — no tool exposes a non-RLS static source.** Every tool reads the tenant-scoped `ProjectSnapshot`
  (Panel/Model/Forecaster/StressTest). Raw estimate/BOQ rows are **not** a copilot tool: the Postgres
  estimate tables are empty and the only estimate data is the workbook the stress test consumes, so a
  raw-BOQ tool would read a static, non-RLS source — inconsistent with the migration decision, so it is
  dropped. The estimate angle is surfaced only through the **computed** `StressTest` report (owning-project
  gated, membership-checked at resolve).

## Migration — copilot reads the project snapshot (tenant-scoped)

- `QsEarlyWarning.Core/Agent/QsAnalyticsTools.cs` — **ctor changes** from `(IModelProvider, WatchlistScoringService)`
  to **`(ProjectSnapshot snapshot, WatchlistScoringService scoring)`**. All tools read `snapshot.Panel`,
  `snapshot.Model`, `snapshot.Forecaster`, `snapshot.StressTest`. Tools are now a **per-request** object,
  not a singleton. `GetEvmSnapshot` is **stripped of its EAC/VAC fields** (G3) — it returns only CV/CPI/SPI;
  EAC/VAC move to the flagged `DirectionalEac` tool.
- `QsEarlyWarning.Core/Agent/IQsCostCopilotAgent.cs` — `AskAsync` gains the tools param:
  `Task<CopilotAskResult> AskAsync(string question, IReadOnlyList<CopilotTurn> history, QsAnalyticsTools tools, CancellationToken ct)`
  (both types are in `Core.Agent`, so Core keeps zero MAF dependency).
- `QsEarlyWarning.Agent/ClaudeQsCostCopilotAgent.cs` — drop the `_tools` field; `BuildAgent(tracker, tools)`
  builds the `ChatClientAgent` from the passed tools. `DisabledCopilotAgent` ignores the param.
- `QsEarlyWarning.Agent/AgentServiceCollectionExtensions.cs` — stop registering `QsAnalyticsTools` (it is
  built per-request now); keep the singleton `IChatClient` + agent. Model id stays configurable
  (`Copilot:Model`, default `claude-sonnet-5`; idea-4 allows Opus 4.8 as orchestrator — unchanged here).
- `QsEarlyWarning.Web.API/Controllers/CopilotController.cs` — inject `IProjectSnapshotRegistry` +
  `ProjectDirectory` + `TenantContext` + `WatchlistScoringService` + `IQsCostCopilotAgent`. Copy
  `DashboardController.Resolve` (401/403/404), build `new QsAnalyticsTools(snapshot, scoring)`, pass to
  `AskAsync`. On a disabled agent / unconfigured key, the existing "not configured" message still returns.
- `QsEarlyWarning.Core/Registry/ProjectSnapshotRegistry.cs` — **no change.** The copilot reads only the
  existing snapshot artifacts (`Panel`, `Model`, `Forecaster`, `StressTest`). Raw estimate/BOQ rows are
  **not** exposed (see G9 + out-of-scope): the Postgres estimate tables are empty and the only estimate
  data is the workbook the stress test already consumes, so a raw-BOQ tool would read a non-RLS static
  source — dropped from this build.
- `QsEarlyWarning.Core/StressTest/*` (idea-3) — **additively enriched** so `StressFlagsForPackage` is
  implementable from the computed report alone: add `Package` to `ReconciliationResult` (resolved from the
  item's mapping → `EstimatePackage`) and `SourceItemRefs` (BOQ item refs) to `AssumptionFlag` — including
  `OutputNormTopPercentile`, which today only carries `norm {NormCode}` (resolve the item refs of the
  package's lines using that norm). The engine populates them during generation; existing idea-3 tests stay
  green. This gives the copilot tool package-scoped filtering **and** citable item-ref row keys (G7).

## New tools (all on the snapshot; every tool returns a `sources` provenance block)

Each tool returns its data **plus** `sources { sheet, resolvedPeriod, filter, excludedCount, rowIds[] }`
so G7 holds. Existing 4 tools gain the `sources` block; new tools:

- **`ForecastIncrementalSpend(bccId)`** — projects `snapshot.Forecaster.ForecastCentre(bccId)` onto an
  **allowlisted DTO** carrying ONLY origin period, trust badge, and the h=1,2,3 P10/P50/P90 **increments**.
  The forecaster's `DirectionalFinalCost` (and any final-cost field) is **deliberately not mapped** — the
  forecast tool cannot emit a final-cost number (G3). **Validated** (idea-2). Null forecaster / unknown bcc
  → typed clarification. `sources.sheet = "9_HISTORICAL_DATA (forecast model)"`.
- **`DirectionalEac(bccId, periodId)`** — the **sole** source of final-cost figures. **Precondition:** if
  BAC or CPI is missing/non-finite or `CPI ≤ 0`, return typed `available:false` (no EAC/VAC, no division).
  Otherwise returns EAC (`BAC/CPI`) **and** VAC (`BAC − EAC`), both `validated:false` with the note
  "directional BAC/CPI extrapolation, not a validated forecast" (G3).
- **`ResourceSplit(bccId, periodId)`** — AcManpower/AcMaterial/AcEquipment/AcSubcontract + shares from the
  row; excludes/annotates a null-AC row.
- **`ProjectEvm(periodId, discipline?, packageCode?)`** — aggregated ratios over the rows in scope (G2/G4),
  returned as **two independent blocks** because eligibility differs per ratio:
  `cpi { available, value = sumEv/sumAc, sumEv, sumAc, includedCount, excludedCount, rowIds[] }` and
  `spi { available, value = sumEv/sumPv, sumEv, sumPv, includedCount, excludedCount, rowIds[] }`, plus the
  resolved filter. **Empty-scope guard:** each block returns `available:false, value:null` when its
  `includedCount == 0` or its denominator sum `≤ 0` (never a divide-by-zero / NaN / Infinity).
  (`CopilotSources.ExcludedCount` is left null for this tool — the per-ratio counts live in these blocks;
  see the evidence section.) The aggregation-trap tool.
- **`StressFlagsForPackage(packageCode)`** — reads the (enriched) `snapshot.StressTest` (idea-3, a
  **computed** report already hung on the per-project snapshot): Class-2 assumption flags filtered by
  `AssumptionFlag.Package` + Class-1 tie-out status filtered by the new `ReconciliationResult.Package`;
  `sources.rowIds` = the **union** of the filtered flags' `SourceItemRefs` **and** the filtered
  reconciliation items' `Scope` (item refs) — so the tie-out answer cites its BOQ items too.
  `available:false` when the project has no estimate workbook (non-owning project).

The `ClaudeQsCostCopilotAgent.BuildAgent` tool list grows to include the five new `AIFunctionFactory.Create`
entries (`ForecastIncrementalSpend`, `DirectionalEac`, `ResourceSplit`, `ProjectEvm`, `StressFlagsForPackage`);
the system prompt (`CopilotPrompts.System`) is extended with the G2/G3 rules and "always report the resolved
filter + excluded count". **No `query_boq`** — raw BOQ rows have no RLS Postgres source (G9).

## Evidence / trace enrichment

- `Core/Agent/IQsCostCopilotAgent.cs` — extend `CopilotEvidence(Tool, Detail)` to
  `CopilotEvidence(Tool, Detail, CopilotSources? Sources)` with
  `CopilotSources(string? Sheet, int? ResolvedPeriod, string? Filter, int? ExcludedCount, IReadOnlyList<string> RowIds)`.
  `ExcludedCount` is nullable: single-scope tools set it; `ProjectEvm` leaves it null and carries its
  per-ratio `cpi`/`spi` counts in the tool payload (the UI reads those for the aggregation answer).
- `ClaudeQsCostCopilotAgent.ToolCallTracker` / `ToolMiddleware` — the middleware already wraps invocation;
  capture the tool **result** and, if it carries a `sources` object, attach it to the recorded evidence
  (best-effort deserialize; absent → null). Args still recorded in `Detail`.
- `Web.API/Contracts/Dtos.cs` — `CopilotEvidenceDto` gains a `Sources` sub-DTO; `CopilotController` maps it.

## Frontend — proactive opener + sources panel

- `src/components/Copilot.tsx` — on mount, render a **proactive drift-watchlist panel at the top** by
  reusing the existing `Watchlist` data (`api.watchlist(period, k)`) with a one-line "N centres drifting
  this period" summary, **before** any question (kills the "bare Q&A box" critique). Chat sits below.
- Replace the 3 static suggestions with suggestions derived from the current watchlist's top centres
  (e.g. "Explain the drift risk for {topBcc}", "Next-period spend forecast for {topBcc}").
- Render the enriched **sources panel** per assistant turn: tool, resolved period/filter, excluded-row
  count, and the source row IDs (expandable), reusing `.evidence`/`.chip`/`.card` classes.
- `src/api/client.ts` — extend `CopilotEvidence` type with `sources`; add types as needed. The copilot may
  move to its own top-level tab (or stay in "Model & Copilot") with the opener; keep it one stack.

## Eval — fixed question set (idea-4's measured artifact)

- **Deterministic ground-truth harness** `tests/QsEarlyWarning.Tests/CopilotEvalTests.cs` (xUnit, no LLM,
  CI-safe): a fixed **15–20 question** table, each with an **independently-computed** ground truth (raw
  LINQ over the panel/estimate in the test, G8) asserted against the corresponding **tool** output —
  numeric value, cited row IDs, resolved period/filter, and excluded-row count. Covers the six adversarial
  cases: ambiguous/aliased period, invalid BCC id (typed error), NOT STARTED rows, zero-AC/EV row,
  **weighted-vs-unweighted CPI** (`project_cpi` = `sum(EV)/sum(AC)` and asserted **≠** mean-of-per-row CPI),
  and a cross-sheet question (forecast / stress). **Both** aggregated ratios are ground-truthed separately
  via `ProjectEvm`'s `cpi`/`spi` blocks — CPI `sum(EV)/sum(AC)` and SPI `sum(EV)/sum(PV)` — each asserted
  `≠` its mean-of-per-row form, with a zero-EV/positive-AC row asserted **included** in the CPI sum and its
  per-ratio counts + cited row keys checked (G4/G7). An **all-NOT-STARTED filter** case asserts each block
  returns `available:false, value:null` (no divide-by-zero). G3 cases assert: **`GetEvmSnapshot` exposes no EAC/VAC field**;
  `DirectionalEac` returns EAC+VAC `validated:false` and returns `available:false` when CPI is
  missing/`≤0`; and the **forecast tool DTO carries no final-cost field**. This proves tool correctness +
  the aggregation + validated/directional rules.
- **Opt-in live-LLM runner** `tests/QsEarlyWarning.Tests/CopilotLiveEvalTests.cs` — gated on
  `ANTHROPIC_API_KEY` (soft-return when absent, like the Testcontainers pattern): runs the agent over the
  question set and asserts it calls the expected tool with the right args and the narrated number matches
  ground truth. Not a CI gate — the demo / time-to-answer story. The no-tools LLM stays a safety demo only.
- Build the ProjectSnapshot in tests from the Excel panel (mirror the registry's `Build`: panel →
  `RollingOriginEvaluator().Train` → fit forecaster → stress test) via a small test helper, so the eval
  runs without Postgres.

## Verification

1. `dotnet build QsEarlyWarning.sln`; `cd frontend/qs-early-warning && npx tsc --noEmit`.
2. `dotnet test` — the deterministic eval harness + updated `CopilotToolScopeTests` (rewired to the
   snapshot ctor) + existing suite all green. (Db.Tests needs Docker — unchanged.)
3. Run API (`:5070`) + dashboard (`:5173`). With **no** `ANTHROPIC_API_KEY`, `POST /copilot/ask` returns
   the "not configured" message for a member (**200**) and **403** for a non-member (`X-User-Id: 2`) —
   proving G6 (RLS before LLM). With a key set, spot-check a real question returns an answer + sources.
4. Browser (Playwright MCP): open the copilot, confirm the **proactive watchlist** renders on load, ask a
   question, confirm the answer + **sources panel** (resolved filter + excluded count + row IDs) render;
   screenshot; only benign console noise.
5. **Report honestly:** the deterministic harness is the measured artifact (tool correctness + the
   `sum(EV)/sum(AC)` rule + adversarial handling); the live-LLM comparison is a demo, reported as such.

## Out of scope / guardrails

- No scheduled/pushed alerts (the watchlist opener is pull-on-load, not a cron push); no multi-tower.
- The model never does arithmetic (G1); the live-LLM eval is never a CI gate (needs a key, costs tokens).
- No new detection/forecast/stress *analytics* — the copilot is the interface/traceability layer over the
  existing validated modules (idea-1/2/3), not a new analytic.
- **No `query_boq` / raw-BOQ tool** (G9): deferred until an RLS-scoped Postgres estimate graph exists
  (today the estimate tables are empty and the workbook estimate is a static, non-RLS source). The estimate
  is surfaced only via the computed `StressFlagsForPackage` tool.
- No secrets in appsettings; the API key comes from env / user-secrets, and the disabled-agent path keeps
  the endpoint alive without one.

## Codex Review

### Round 1 (2026-07-08) — blocking findings

1. G2 stated aggregated **CPI/SPI** = `sum(EV)/sum(AC)` — wrong for SPI (would bake bad arithmetic into
   the tool + tests). Fix: CPI = `sum(EV)/sum(AC)`; SPI = `sum(EV)/sum(PV)`; separate tests per ratio.
2. `query_boq` was enabled by keeping `EstimateModel` from `_estimate.TryLoadForProject`, i.e. the static
   Excel estimate source (not an RLS Postgres estimate graph) — violates the Postgres-migration decision.
   Fix: source it from RLS Postgres, or drop/mark-unavailable the raw-BOQ tool.

#### Round-1 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | SPI aggregation wrong | **G2 rewritten**: CPI `sum(EV)/sum(AC)` and SPI `sum(EV)/sum(PV)` as distinct denominators; tool renamed `ProjectEvm` returning both; eval ground-truths each ratio separately and asserts each ≠ mean-of-rows. |
| 2 | `query_boq` non-RLS static source | **Dropped `query_boq`** and the `ProjectSnapshot.Estimate` addition (Postgres estimate tables are empty; workbook estimate is static/non-RLS). New **G9** forbids any tool reading a non-RLS static source; the estimate is surfaced only via the computed `StressFlagsForPackage` (owning-project gated). Recorded in out-of-scope. |

### Round 2 (2026-07-08) — blocking findings

1. `ProjectEvm` still excluded zero-EV rows and used one eligibility set for both ratios — a zero-EV /
   positive-AC row is a valid bad signal; dropping it biases ratios upward. Fix: exclude only
   missing/non-finite or zero-denominator rows, ratio-specific, with per-ratio included/excluded counts.
2. G3 bypassable — `GetEvmSnapshot` still exposed EAC/final-cost while only `DirectionalEac` was flagged.
   Fix: strip EAC (and VAC) from `GetEvmSnapshot` or flag every final-cost field; add eval coverage.

#### Round-2 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Zero-EV exclusion biases ratios | **G2/G4 rewritten**: eligibility is ratio-specific and keys on the denominator only (CPI: AC>0; SPI: PV>0); zero-numerator rows are kept; `ProjectEvm` returns per-ratio included/excluded counts; eval asserts a zero-EV/positive-AC row is counted in CPI. |
| 2 | `GetEvmSnapshot` EAC bypass | **G3 rewritten**: `GetEvmSnapshot` stripped of EAC **and** VAC (returns only CV/CPI/SPI); `DirectionalEac` is the sole source of EAC/VAC, both `validated:false`; eval case asserts no unflagged final-cost field exists. |

### Round 3 (2026-07-08) — blocking findings

1. `ProjectEvm` returned one `sum(EV)` + one `ExcludedCount` while CPI/SPI now have different eligibility —
   ambiguous/misleading totals. Fix: per-ratio `cpi{sumEv,sumAc,included,excluded,rowIds}` +
   `spi{sumEv,sumPv,included,excluded,rowIds}`; update sources/UI/eval.
2. `ForecastIncrementalSpend` returned `ForecastCentre`, whose DTO includes `DirectionalFinalCost` —
   bypasses G3. Fix: allowlisted forecast DTO (origin/trust + h1-h3 bands only); eval asserts no final-cost.
3. `DirectionalEac` lacked a zero/invalid-CPI precondition. Fix: missing/non-finite/`CPI≤0` →
   `available:false`, no EAC/VAC; eval.

#### Round-3 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Single vs per-ratio totals | `ProjectEvm` now returns independent `cpi`/`spi` blocks (each with sumEv, denominator sum, included/excluded, rowIds); `CopilotSources.ExcludedCount` documented nullable (per-ratio counts live in the payload); eval checks both blocks. |
| 2 | Forecast tool final-cost bypass | `ForecastIncrementalSpend` maps an **allowlisted** DTO (origin, trust, h1-h3 P10/P50/P90 only); `DirectionalFinalCost` never mapped; eval asserts no final-cost field from forecast tools. |
| 3 | `DirectionalEac` division guard | Precondition added: BAC/CPI missing/non-finite or `CPI≤0` → typed `available:false`, no EAC/VAC; eval covers it. |

### Round 4 (2026-07-08) — blocking findings

1. G7 source row IDs not implementable — `CostCentrePeriod` carries no `cost_centre_period_id` / Excel row
   number, yet eval asserts `rowIds`. Fix: add a real row identifier the panel can carry, or cite an
   identifier that already exists.
2. `ProjectEvm` still lacked a no-eligible-row guard per ratio — an all-missing/`≤0`-denominator filter →
   divide-by-zero/NaN/Infinity. Fix: each block returns `available:false, value:null` when
   `includedCount == 0` or denominator sum `≤ 0`; add an all-NOT-STARTED eval case.

#### Round-4 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Row IDs not implementable | **G7 rewritten** to cite the row's **natural composite key** — `"{BccId}@P{PeriodId}"` for panel rows (loader-enforced unique; deterministically locates the row) and the BOQ item ref for estimate rows. No surrogate id / Excel row number / loader / schema change; eval checks the keys against actual rows. |
| 2 | Divide-by-zero on empty scope | `ProjectEvm` `cpi`/`spi` blocks gain `available:false, value:null` when `includedCount==0` or denominator sum `≤0`; an all-NOT-STARTED eval case asserts it. |

### Round 5 (2026-07-08) — blocking findings

1. `StressFlagsForPackage` not implementable from `snapshot.StressTest` alone: `ReconciliationResult` has
   no `Package`, and `AssumptionFlag` (esp. `OutputNormTopPercentile`) doesn't always carry a BOQ item ref
   — breaking package-specific tie-out and the G7/eval source-key checks for estimate rows. Fix: enrich the
   computed report with structured `Package` + `SourceItemRefs` during generation; the tool reads only that.

#### Round-5 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Stress tool not implementable | Idea-3 `StressTestReport` **additively enriched**: `ReconciliationResult.Package` (from mapping→EstimatePackage) + `AssumptionFlag.SourceItemRefs` (BOQ item refs, incl. resolving them for `OutputNormTopPercentile`). `StressFlagsForPackage` filters by `Package` and cites `SourceItemRefs` as `rowIds`; existing idea-3 tests stay green. |

### Round 6 (2026-07-08) — blocking findings

1. `StressFlagsForPackage` under-cited Class-1 tie-out rows: `sources.rowIds` was only the flags'
   `SourceItemRefs`, so a package tie-out answer could omit the BOQ item refs behind reconciliation items.
   Fix: `sources.rowIds` = union of filtered `AssumptionFlag.SourceItemRefs` and filtered
   `ReconciliationResult.Scope` item refs.

#### Round-6 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Tie-out rows under-cited | `StressFlagsForPackage.sources.rowIds` now the **union** of the filtered assumption-flag `SourceItemRefs` and the filtered reconciliation `Scope` item refs. |

### Round 7 (2026-07-08) — **CLEAN**

> **"no remaining blocking findings."** Codex confirmed the six prior findings are reconciled and
> spot-checked the code: the natural `BccId@P{PeriodId}` citation key is implementable on the domain row
> shape, the idea-3 stress enrichment is additive (not structurally impossible), snapshot caching stays
> tenant-safe because the controller resolves membership before using a warm cache, and the migration
> (singleton→per-request tools over the snapshot) is invasive but straightforward.

**Review loop closed clean at round 7.** Preserve during implementation: tools-compute/model-narrates
(no LLM arithmetic), CPI=`sum(EV)/sum(AC)` & SPI=`sum(EV)/sum(PV)` with ratio-specific denominator-only
eligibility + empty-scope guard, the validated-forecast vs directional-EAC boundary (no bypass via
`GetEvmSnapshot`/forecast DTO), natural-key source citations, RLS resolve before the LLM, and the
independent-ground-truth eval.
