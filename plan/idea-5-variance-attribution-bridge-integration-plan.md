# Plan — Integrate Idea 5 (Variance Attribution Bridge) into the platform

## Context

Idea 5 (`ideas/idea-5-variance-root-cause.md`, reframed to **Variance Attribution Bridge**) turns a red
flag into an **attribution**: not just "BCC-STR-12 is over by 85k", but *which resource category* drives
the cost variance and whether schedule is off — with the cause left as a **hypothesis for the QS to
confirm**. It is the "why" behind idea-1's "which", and it is **not a standalone product**: it ships as
the **drill-down behind idea-1's watchlist** and a **tool under idea-4's copilot**.

Two honest lanes, never folded together:
- **Cost/efficiency lane (CV).** `CV_AED = EV − AC`, decomposed by resource: `CV = Σ_r (EV_r − AC_r)`
  where `EV_r = EV × norm_share_r` (norm-implied resource share from `4_ESTIMATE_DATASHEET`) and `AC_r`
  is the recorded actual split (`AC_{Manpower,Material,Equipment,Subcontract}_AED`). Size each gap by
  `AC_r / EV_r` ("manpower ran ~1.8× its norm-implied budget"). **No price-vs-productivity claim** (no
  hours/quantities/rates in the data) and **no quantity split of CV** (CV is measured at the earned
  quantity — it has no quantity term).
- **Schedule/progress lane (SV).** `SV_AED = EV − PV` (+ SPI), shown alongside CV, not folded in. Called
  schedule/progress, never scope.

Anchored by a **tie-out**: `Σ CV_r + unexplained_residual = CV_AED` and `SV_AED = EV − PV`, exact; any
package that doesn't tie shows an "unexplained residual", never hidden.

**Decisions (confirmed via AskUserQuestion):**
1. **Norm shares come from a computed resource-mix artifact on the per-project snapshot** (owning-project
   gated), built from the same estimate the stress test already loads — consistent with idea-4's
   Postgres/RLS migration and its "no raw static source" guard (the mix is a computed *aggregate*, not raw
   BOQ rows).
2. **Wire both consumers:** a read-only variance endpoint + a **waterfall card reachable from the
   watchlist** (click a red row → attribution card), **and** an `ExplainVariance` tool added to the
   idea-4 copilot.
3. **SV lane ships as the monetary `SV_AED = EV − PV` (+ SPI)** — the period-grain earned-vs-planned
   *quantity* comparison is honestly deferred (no period-grain qty is loaded; the DB `earned_qty` is null).

**Save location:** this doc at `plan/idea-5-variance-attribution-bridge-integration-plan.md`.

## Correctness & honesty guards (non-negotiable — front-loaded)

- **G1 — attribution, not cause; no CV quantity/price split.** `CV_AED = EV − AC` is the cost/efficiency
  variance *at the earned quantity* — it has **no quantity term** (quantity/schedule is the SV lane), and
  on this data it **cannot** be split into price vs productivity (no labour hours, per-resource
  quantities, or purchase rates). The engine names the dominant *resource contributor* and its
  `AC_r/EV_r` ratio; any stated cause is a **hypothesis** the narrative labels as such.
- **G2 — tie-out is the trust anchor.** With `CV_r = EV_r − AC_r` and `Σ EV_r = EV`, `Σ CV_r = EV −
  Σ AC_r`; to tie to `CV = EV − AC` the **additive** residual is `ResidualCv = Σ_r AC_r − AC` (the signed
  amount by which the four splits over/under-cover AC). Assert `|Σ_r CV_r + ResidualCv − CV_AED| ≤ tol`
  and `|SV_AED − (EV − PV)| ≤ tol` in code. The residual is surfaced (as "unexplained residual — AC the
  four splits don't attribute"), never hidden. `norm_share_r` is normalized to sum to 1 so EV allocation
  introduces no leakage.
- **G3 — assumption-based attribution badge + evidence-needed field.** The EV→resource allocation uses
  *estimate shares*, not measured actuals — every card/answer carries an `assumptionBased:true` badge and
  an `evidenceNeeded` field naming what would confirm the cause (manpower → labour hours + wage rates;
  material → invoices + quantities; equipment → plant hours + hire rates; subcontract → valuations + scope).
- **G4 — live-row gate + finite guard.** Only diagnose rows where `EvAed`, `AcCumulative`, `PvAed` are all
  finite (the domain money fields are `double?` — never coerce a null to 0) **and** `EvAed > 0` (a
  norm-implied split is meaningful). NOT STARTED / zero-earned / missing-figure rows return
  `available:false` with a note (no absurd `AC_r/EV_r` multipliers, no silent zeros).
- **G5 — EP- packages only (explicit variance-side gate).** The Postgres panel loader filters by
  `project_id` **only** (unlike the Excel loader, it does not require `package_code LIKE 'EP-%'`), so the
  variance side must not assume it. The attributor and the mix aggregation both **explicitly gate**
  `PackageCode.StartsWith("EP-", OrdinalIgnoreCase)` — a non-`EP-` centre returns `available:false` and
  never contributes to the mix. Asserted in a test.
- **G6 — SV is schedule/progress, monetary only.** The SV lane is `SV_AED = EV − PV` (+ SPI), labelled
  schedule/progress (not scope). The period-grain earned-vs-planned *quantity* comparison is **not** shown
  (data not loaded) — the card says so rather than faking a same-grain comparison. Because we compare
  monetary EV vs PV (same basis), there is no cumulative-vs-period grain error to make.
- **G7 — RLS tenant boundary before any read.** Both consumers resolve the snapshot via the tenant
  `Resolve` pattern (401/403/404) before computing anything; the copilot already does this per idea-4.
- **G8 — resource mix is a computed aggregate, owning-project gated.** `ResourceMix` (per-package
  resource-cost *shares*) is computed in `Build` from the estimate; null for non-owning projects. When
  null, the **CV-by-resource breakdown is unavailable** but the CV_AED total + the SV lane still render
  (they need only the panel).

## Core engine — `src/QsEarlyWarning.Core/Variance/` (new)

- `VarianceModels.cs`:
  - `ResourceContribution(string ResourceType, double NormShare, double EvR, double AcR, double CvR, double? TimesNormBudget)`
    — `EvR = EV × NormShare`, `AcR` = panel split, `CvR = EvR − AcR`, `TimesNormBudget = AcR / EvR` (guarded).
  - `VarianceBridge(string BccId, int PeriodId, bool Available, string? UnavailableReason, string? Package, string? Discipline, double? Bac, double? Pv, double? Ev, double? Ac, double? CvAed, double? SvAed, double? Spi, IReadOnlyList<ResourceContribution> Contributions, string? DominantResourceType, double? UnexplainedResidual, bool TiesOut, bool ResourceBreakdownAvailable, bool AssumptionBased, string? EvidenceNeeded, IReadOnlyList<string> Notes)`
    — **all computed money fields are nullable**; a missing/non-`EP-`/non-live/null-money row is
    represented as `Available:false` + `UnavailableReason` with null money + empty contributions (never
    zero-coerced), not as `null` or a throw.
- `VarianceAttributor.cs` — pure, deterministic:
  - `Attribute(IReadOnlyList<CostCentrePeriod> panel, IReadOnlyDictionary<string, IReadOnlyDictionary<string,double>>? mix, string bccId, int periodId) → VarianceBridge`
    (always returns a shaped bridge; the `Available` flag carries the state — never returns `null`).
  - Finds the `(bccId, periodId)` row; returns `Available:false` + `UnavailableReason` when the row is
    missing, its `PackageCode` is not `EP-` (G5), `EvAed`/`AcCumulative`/`PvAed` are **not all finite**
    (the domain fields are `double?` — never coerce a null to 0), or `EV ≤ 0` (G4).
  - `Ev = EvAed`, `Ac = AcCumulative`, `Pv = PvAed`, `CvAed = EvAed − AcCumulative` (matches recorded
    `CvAed`; asserted in tests), `SvAed = EvAed − PvAed` (G6).
  - If `mix[Package]` present (G8): normalize shares to sum 1; for each of the four canonical resource
    types build a `ResourceContribution`; `UnexplainedResidual = Σ AcR − Ac` (additive tie-out term, G2);
    `TiesOut = |CvAed − (Σ CvR + UnexplainedResidual)| ≤ tol`.
    - **Dominant contributor by variance direction (not always "most negative"):** for an overrun
      (`CvAed < 0`) the dominant is `min CvR`; for a favorable variance (`CvAed > 0`) it is `max CvR`.
      **If `|UnexplainedResidual|` exceeds the top resource's `|CvR|`**, report the dominant as
      `"unexplained residual"` rather than a resource (honest when the splits don't cover AC).
    - If mix absent: `Contributions = []`, `ResourceBreakdownAvailable = false`, tie-out on the SV lane +
      CV total only.
  - `EvidenceNeeded` from the dominant resource (G3); `AssumptionBased = true` whenever a breakdown is shown.
  - Canonical resource types + panel accessors mirror idea-4's `ResourceSplit` map
    (MANPOWER→AcManpower, MATERIAL→AcMaterial, EQUIPMENT→AcEquipment, SUBCONTRACT→AcSubcontract).

## Registry — computed resource mix on the snapshot

- `ProjectSnapshotRegistry.cs` — add `ResourceMix` (`IReadOnlyDictionary<string, IReadOnlyDictionary<string,double>>?`)
  to `ProjectSnapshot`. In `Build`, inside the **same** try-block that already loads the estimate for the
  stress test, also aggregate `Σ ResourceCost` by `(EstimatePackage, canonical ResourceType)` → per-package
  shares (only `EP-` packages, G5), and hang it on the snapshot. Null for non-owning projects (estimate
  null). No new estimate load, no raw estimate rows on the snapshot (G8, idea-4 G9 preserved).

## API — `src/QsEarlyWarning.Web.API/Controllers/VarianceController.cs` (new), route `api/v1/variance`

Use the **strongest** tenant sequence on the platform — the one `WatchlistController` already uses (not
`DashboardController.Resolve`, which is `private` and only checks directory membership): inject
`IProjectSnapshotRegistry` + `IProjectPanelSource` + `ProjectResolver` + `TenantContext`, then
**`ProjectResolver.ResolveAsync(slug)` → `IProjectPanelSource.IsAuthorizedAsync(projectId, userId)` (RLS
membership probe → 403) → `registry.GetOrBuildAsync`** (401 when unauthenticated). This closes the
project-keyed snapshot-cache gap (a cache hit is authorized per-request, not trusted by project id alone).
- `GET /variance?bcc=&period=` → `VarianceBridgeDto` (positional record in `Contracts/`, mirrors the core
  bridge incl. `available`/`unavailableReason` + nullable money; non-finite → null). Returns
  `available:false` (200) when the row is missing / non-`EP-` / not live / has null money (G4/G5);
  `available:true` with `resourceBreakdownAvailable:false` when the project has no estimate mix (G8) and
  CV/SV totals are valid.

## Copilot tool (idea-4) — `ExplainVariance`

- Add `ExplainVariance(string bccId, int periodId)` to `QsAnalyticsTools` (reads `snapshot.Panel` +
  `snapshot.ResourceMix` via `VarianceAttributor`); returns the dominant contributor, CV/SV, the
  `AC_r/EV_r` ratio, the assumption badge + evidence-needed, and a `sources` block (sheet
  `9_HISTORICAL_DATA` + estimate mix; rowId `"{BccId}@P{PeriodId}"`). Register it in
  `ClaudeQsCostCopilotAgent.BuildAgent` and add one line to `CopilotPrompts.System` ("to explain *why* a
  centre is over/under, call ExplainVariance — it attributes CV by resource; the cause is a hypothesis").

## Frontend — waterfall card + watchlist drill-through

- `src/api/client.ts`: add `variance(bcc, period)` + `VarianceBridge`/`ResourceContribution` types.
- `components/VarianceCard.tsx`: an inline-SVG **variance waterfall** (PV → SV effect → EV → per-resource
  step → AC), dominant bar highlighted, a one-line verbal attribution, the **assumption-based-attribution
  badge**, the **evidence-needed** line, and the **tie-out / unexplained-residual** readout. **Waterfall
  leg sign:** the `EV → AC` legs walk with `AcDelta_r = AC_r − EV_r = −CvR` (and a residual leg
  `AC − ΣAcR = −UnexplainedResidual`), so an overrun bar visually moves the cost *up*; the `CvR` value is
  used for the tie-out **text**, not the bar direction. Reuse the `Spark`/SVG idiom + `.card`/`.tag`/
  `.pill`/`.kpi` classes; **no new chart dependency** (CSP-safe).
- `components/Watchlist.tsx`: add an optional `onSelect?(bccId: string)` prop; when provided, rows become
  clickable (the drill-through). The Watchlist tab wires `onSelect` to open the `VarianceCard` for the
  clicked centre at the current period — idea-1's watchlist → idea-5's attribution card, in place.
- `App.tsx`: in the Watchlist tab, render the selected centre's `VarianceCard` beside/below the list.

## Verification

1. `dotnet build QsEarlyWarning.sln`; `cd frontend/qs-early-warning && npx tsc --noEmit`.
2. `dotnet test` — new `VarianceTests` + existing suite green (66 today). Tests assert, from the workbook
   panel + estimate mix via `TestSnapshot`:
   - **Tie-out (trust anchor, G2):** for live rows, `CvAed ≈ EvAed − AcCumulative` and `Σ CvR +
     (Σ AcR − Ac) == CvAed`; `SvAed == EvAed − PvAed`. On a row whose four splits sum to AC the
     `UnexplainedResidual ≈ 0`.
   - **G5 gate:** a synthetic non-`EP-` centre → `available:false` and contributes nothing to the mix.
   - **Attribution:** `DominantResourceType` equals the hand-derived largest-overrun resource on an
     overrun centre; `TimesNormBudget = AcR/EvR`. Direction cases: a **favorable** row (`CvAed > 0`) picks
     `max CvR`; a **residual-dominant** row (splits don't cover AC) reports `"unexplained residual"`.
   - **Finite guard (G4):** a row with a null `EvAed`/`AcCumulative`/`PvAed` → `available:false`, no throw.
   - **G1/G6:** no field claims a CV quantity or price/productivity split; SV is monetary EV−PV.
   - **G4 gate:** an `EV ≤ 0` / NOT STARTED row → `available:false` (no absurd ratio).
   - **G8:** with a null mix, `resourceBreakdownAvailable:false` but CV/SV totals present.
   - Copilot: `ExplainVariance` returns a bridge with the badge + evidence-needed; unknown bcc → typed error.
3. Run API (`:5070`) + dashboard (`:5173`); `curl /variance?bcc=BCC-…&period=…` → 200 for a member,
   **403 for a non-member**, 401 unauth, `available:false` for a NOT STARTED centre. If a copilot key is
   set, ask "explain BCC-…'s overrun" and confirm it routes to `ExplainVariance` with the assumption badge.
4. Browser (Playwright MCP): open the Watchlist tab, click a red row, confirm the **variance waterfall
   card** renders with the dominant contributor highlighted, the assumption badge, evidence-needed, and the
   tie-out readout; screenshot; only benign console noise.
5. **Report honestly:** the tie-out is the measured artifact; the named contributor is an attribution with
   the cause flagged as a hypothesis; the resource breakdown is owning-project only.

## Out of scope / guardrails

- No price-vs-productivity or quantity-split-of-CV claims (G1); cause stays a hypothesis.
- SV quantity-grain comparison deferred (no period-grain qty loaded); monetary SV_AED only (G6).
- No new estimate load / no raw estimate rows on the snapshot — only the computed `ResourceMix` aggregate.
- No cross-package/cross-tower pattern mining, no norm-correction feedback loop (idea's deferred items).
- No new chart dependency (inline SVG, CSP-safe).

## Codex Review

### Round 1 (2026-07-08) — blocking findings

1. Residual sign breaks the CV tie-out: with `CV_r = EV_r − AC_r` and `ΣEV_r = EV`, `ΣCV_r = EV − ΣAC_r`,
   so the additive residual must be `ΣAC_r − AC` (not `AC − ΣAC_r`) for `ΣCV_r + residual == CV`.
2. G5 relied on a false assumption: `PostgresPanelLoader` filters `project_id` only — the DB does not
   require `package_code LIKE 'EP-%'`. Explicitly gate `EP-` in the attributor/mix, or filter in the view.
3. (Robustness) `DashboardController.Resolve` is private and only checks directory membership; the new
   endpoint should use `WatchlistController`'s stronger `ProjectResolver + IsAuthorizedAsync + registry`
   RLS-probe sequence so a project-keyed cache hit is authorized per-request.

#### Round-1 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Residual sign | **G2 + engine** fixed: `UnexplainedResidual = ΣAcR − Ac`, assert `ΣCvR + UnexplainedResidual == CvAed`. |
| 2 | EP- not enforced on Postgres path | **G5 + engine + mix** now explicitly gate `PackageCode.StartsWith("EP-")`; non-EP → `available:false`, excluded from the mix; test added. |
| 3 | Weaker tenant guard | **API** uses `WatchlistController`'s sequence: `ProjectResolver.ResolveAsync → IProjectPanelSource.IsAuthorizedAsync (403) → registry.GetOrBuildAsync` (401 unauth); no reliance on the private `DashboardController.Resolve`. |

### Round 2 (2026-07-08) — blocking findings

1. Waterfall sign: an `EV → AC` walk using `CvR = EV_r − AC_r` moves overrun bars the wrong way. Chart
   `AcDelta_r = AC_r − EV_r = −CvR` (+ residual leg `−UnexplainedResidual`); keep `CvR` for tie-out text.
2. `DominantResourceType = most negative CvR` mislabels favorable/residual-dominant variance. Pick by
   direction (`CvAed<0` → min, `CvAed>0` → max); if `|residual|` exceeds the top resource, report
   "unexplained residual"; add favorable + residual-dominant tests.
3. `CostCentrePeriod` money fields are `double?`, but the engine used non-null doubles and gated only
   `EV ≤ 0`. Require finite `EvAed`/`AcCumulative`/`PvAed` before attribution; else `available:false`.

#### Round-2 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Waterfall leg sign | Frontend section: `EV→AC` legs use `AcDelta_r = AC_r − EV_r` (+ `−UnexplainedResidual` residual leg); `CvR` used only for tie-out text. |
| 2 | Dominant selection | Engine: dominant by variance direction (overrun→min `CvR`, favorable→max `CvR`); residual-dominant → `"unexplained residual"`; verification adds favorable + residual-dominant cases. |
| 3 | Nullable money fields | **G4 + engine**: require finite `EvAed`/`AcCumulative`/`PvAed` (no null→0 coercion) + `Ev>0`; else `available:false` with a note; test added. |

### Round 3 (2026-07-08) — blocking findings

1. `VarianceBridge` had no `Available` field and non-null money doubles, so missing/non-`EP-`/non-live/
   null-money rows couldn't be represented without zero-coercion. Fix: add `bool Available` +
   `string? UnavailableReason`; make `Bac/Pv/Ev/Ac/CvAed/SvAed/Spi` nullable; `Available:false` (null
   money, empty contributions) before the finite guard passes; `Available:true` +
   `ResourceBreakdownAvailable:false` when CV/SV valid but mix absent.

#### Round-3 reconciliation

| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Missing availability contract | `VarianceBridge`/DTO gain `Available` + `UnavailableReason`; all computed money fields nullable; `Attribute` always returns a shaped bridge (never null) with `Available` carrying the state; unavailable → null money + empty contributions, never zero-coerced. |

### Round 4 (2026-07-08) — **CLEAN**

> **"no remaining blocking findings."** Codex confirmed the core/API symbols and integration points line
> up with the actual tree (under `QsEarlyWarning/`), the tenant sequence matches `WatchlistController`, the
> nullable domain fields are handled, and no EVM-identity / leakage / honesty / build blocker remains.

**Review loop closed clean at round 4.** Preserve during implementation: attribution-not-cause (no CV
quantity/price split), the `Σ CvR + (Σ AcR − Ac) == CvAed` tie-out + `SV_AED = EV − PV`, the EP- gate +
finite guards + `Available` contract, the assumption-based-attribution badge + evidence-needed, the
RLS-probe tenant sequence, and the computed-mix-aggregate (never raw estimate rows) on the snapshot.
