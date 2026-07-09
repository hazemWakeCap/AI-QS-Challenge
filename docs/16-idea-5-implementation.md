# Idea 5 — Variance Attribution Bridge: How It Was Actually Built

> Companion to the product-facing [Feature 5 — Variance Attribution Bridge](05-variance-attribution-bridge.md)
> and the original spec [`ideas/idea-5-variance-root-cause.md`](../ideas/idea-5-variance-root-cause.md).
> This doc is the **engineering reality**: what shipped, where the code lives, the math, and how a QS
> (or a developer) actually uses it. It mirrors the deep dives for
> [Idea 1](12-idea-1-implementation.md), [Idea 2](13-idea-2-implementation.md),
> [Idea 3](14-idea-3-implementation.md), and [Idea 4](15-idea-4-implementation.md).

## From spec to shipped — the short version

The [idea spec](../ideas/idea-5-variance-root-cause.md) started as a "Root-Cause Decomposer" that would
split `CV` into quantity vs rate. Both the CEO-mode review and Codex killed that framing as a **math
error**: `CV_AED = EV − AC` is measured *at the earned quantity*, so it is already a pure
cost/efficiency variance with **no quantity term to split** — and this dataset has only the four
`AC_*_AED` category totals (no labour hours, per-resource quantities, or purchase rates), so it can't be
split into price vs productivity either. The surviving product is a **Variance Attribution Bridge**:
two honest EVM lanes, never folded together, with the named driver kept as a **hypothesis**, not a
proven cause.

- **Cost/efficiency lane (CV):** `CV = EV − AC`, attributed by resource via estimate shares.
- **Schedule/progress lane (SV):** `SV = EV − PV`, monetary, shown *alongside* CV — never folded in.

What shipped follows that framing faithfully, with two deliberate departures:

| Spec said | What shipped | Why |
|-----------|--------------|-----|
| A Python `decompose.py` + a Plotly waterfall | **C# engine in `QsEarlyWarning.Core.Variance`** + one read-only endpoint + a React `VarianceCard` with an **inline-SVG waterfall** | One .NET runtime for the whole product; the waterfall is hand-drawn SVG, no chart dependency. |
| SV lane as `Earned_Qty_Period` vs `Planned_Qty_Period` (physical quantities) | **Monetary `SV = EV − PV`** | The review's own final correction: the recorded `SV_AED = EV − PV` ties out exactly (diff 0), and a monetary lane is what the waterfall needs; physical-quantity comparison is left for a future build. |

Everything else — attribution-not-cause, the additive tie-out anchor, the `EP-`-only + live-only gates,
the assumption-based badge, the evidence-needed field, and the "surface it *from* a flag, never as a
destination" packaging — is implemented as specified.

## The two lanes and the tie-out (the trust anchor)

The whole feature rests on the EVM identities holding exactly on this data (`CV = EV − AC` diff 0,
`SV = EV − PV` diff 0), so the engine is **not re-deriving EVM** — it is *attributing* the already-correct
CV to resources and narrating it. The tie-out is **additive and exact by construction**:

```
Cost/efficiency lane   CV = EV − AC
   per resource r:     EV_r = EV × normShare_r        (norm-implied earned budget for r)
                       AC_r = recorded actual split   (AC_Manpower/Material/Equipment/Subcontract)
                       CV_r = EV_r − AC_r
   tie-out anchor:     Σ CV_r + UnexplainedResidual == CV,   where UnexplainedResidual = ΣAC_r − AC

Schedule/progress lane SV = EV − PV      (monetary, shown alongside — never folded into CV)
```

`UnexplainedResidual` is the part of AC the four recorded splits don't cover; it is **never hidden** —
it's a first-class tie-out term. This is what makes the bridge honest: if the splits don't sum to AC,
the gap surfaces rather than silently distorting a resource's contribution.

## The math, step by step (`VarianceAttributor`)

A pure, deterministic engine (`Core/Variance/VarianceAttributor.cs`). For one `(BccId, PeriodId)`:

### Step 1 — resolve and gate

- Find the panel row for `(BccId, PeriodId)`; missing → `Available:false`.
- **`EP-` packages only** (`PackageCode` starts `EP-`) — the Postgres loader doesn't enforce it, so the
  engine does. This filters out the junk `AC_Cumul` roll-up block (non-`EP-` codes).
- **Finite money + live**: `EV`, `AC_cumulative`, `PV` must all be finite (never coerce a null to 0),
  and `EV > 0`. A NOT STARTED / zero-earned row is `Available:false` ("no meaningful variance") — this
  is the gate that stops `AC_r ÷ earned qty` from blowing up.

### Step 2 — the lanes

```
CV = EV − AC          # == the recorded CvAed (asserted in tests)
SV = EV − PV
SPI = EV ÷ PV         # null when PV ≤ 0
```

### Step 3 — resource contributions (normalized shares)

The resource mix for the package (`mixForPkg`) is the estimate's `Σ Resource Cost` by canonical resource
type. Shares are **normalized to sum to 1** first, so allocating EV introduces no leakage:

```
share_r = max(0, rawShare_r) ÷ Σ max(0, rawShare)
EV_r    = EV × share_r
AC_r    = recorded split for r (0 if missing)
CV_r    = EV_r − AC_r
TimesNormBudget_r = AC_r ÷ EV_r      # "ran ~1.8× its norm-implied budget"; null if EV_r ≤ 0
```

If **no mix** exists for the package, the engine returns CV/SV **totals only** with
`ResourceBreakdownAvailable:false`, `AssumptionBased:false`, and a note — a graceful, honest fallback.

### Step 4 — the dominant contributor (with the residual rule)

Rank contributions by variance *direction* — for an overrun (`CV < 0`) the **most negative** `CV_r` is
top; for a favourable variance the **most positive**. Then the residual rule:

```
if |UnexplainedResidual| > |topResource.CV_r|:
    dominant = "unexplained residual"     # the splits don't cover AC — no single resource can be blamed
else:
    dominant = topResource.ResourceType
```

So the dominant result is a resource category **unless** the unexplained residual outweighs it, in which
case it's honestly reported as `"unexplained residual"`.

### Step 5 — the honesty markers

- **`AssumptionBased: true`** whenever the resource breakdown is produced — because the EV→resource
  allocation uses **estimate shares, not measured actuals**, so it can only identify the dominant
  *contributor*, not separate price from productivity.
- **`EvidenceNeeded`** names what would confirm the cause per resource: manpower → *labour hours + wage
  rates*; material → *supplier invoices + delivered quantities*; equipment → *plant hours + hire rates*;
  subcontract → *subcontract valuations + agreed scope*.
- A note is always added: *"Attribution uses estimate resource shares (assumption-based), not measured
  actuals. Cost is a hypothesis to confirm."*

The tolerance on both tie-out checks is `0.5` AED; values are rounded to 3 dp.

## The resource mix — a computed aggregate (`BuildResourceMix`)

The estimate shares come from `ProjectSnapshotRegistry.BuildResourceMix` (built once when the snapshot
is built, only for the estimate's owning project): it aggregates `Σ Resource Cost` by
`(EP- package, canonical resource type)` over `4_ESTIMATE_DATASHEET`. Because that datasheet is already
**Output-Norm corrected** (see `data/README.md`), the aggregation gives the correct expected split with
no extra math. It is a **computed aggregate** hung on the snapshot — raw estimate rows are never
exposed. When no estimate exists for a project, `ResourceMix` is `null` and the bridge degrades to
CV/SV totals only.

## Validating the results — `dotnet test` (`VarianceTests`)

The engine's credibility is the tie-out, pinned by an automated suite
(`tests/QsEarlyWarning.Tests/VarianceTests.cs` — **7 tests, all passing**):

| Test | What it locks down |
|------|--------------------|
| `Resource_mix_is_present_for_the_owning_project` | the estimate mix loads (>50 packages) |
| `Bridge_ties_out_to_the_AED` | **the trust anchor** — `CV == EV − AC`, `SV == EV − PV`, and `Σ CV_r + residual == CV`, all to the AED |
| `Dominant_contributor_follows_the_variance_direction_and_residual_rule` | dominant = top-by-direction unless the residual outweighs it → `"unexplained residual"` |
| `Attribution_is_flagged_assumption_based_with_evidence_needed` | the assumption badge + evidence-needed field + the note are present |
| `Non_EP_row_is_unavailable` | a non-`EP-` package is `Available:false` (junk-block guard) |
| `Missing_money_or_not_started_row_is_unavailable_no_throw` | `EV = 0` / null money → `Available:false`, never a throw |
| `Mix_absent_still_gives_CV_and_SV_totals_without_resource_breakdown` | no mix → CV/SV totals, `ResourceBreakdownAvailable:false` |

> The leakage discipline the CEO review demanded (never grade against `Alert_Level` or the same CV being
> explained) is respected: the tests grade against the **tie-out identity** and the deterministic
> selection rule, not against any signal derived from CV.

## How users make use of it

The spec's "subtraction default" — a QS reaches a variance-attribution view **from** a flag, never as a
destination — is exactly how it's wired: it is not a standalone app, but the drill-down behind the
watchlist and a copilot tool.

### A. The click-through from the watchlist (primary — for the QS)

In the app, clicking a row in the **Watchlist** (`onSelect`) sets the selected centre and renders the
**`VarianceCard`** for it (`App.tsx`). The card shows:

- A **fact-first attribution line**: *"Over by 85k (CV). Schedule on-plan (SV ≈ 0). **Manpower** is the
  dominant cost-variance contributor at ~1.8× its norm-implied budget."* (or "Unexplained residual
  dominates…" when the splits don't cover AC).
- **Honesty markers**: an amber **"assumption-based attribution"** badge and the **evidence-to-confirm**
  field.
- KPI tiles (PV / EV / AC / CV) and, when the breakdown is available, an **inline-SVG waterfall**
  (`PV → +SV → EV → per-resource legs → residual → AC`, biggest bar outlined) plus a CV-by-resource
  table (`EV_r`, `AC_r`, `CV_r`, ×norm).
- A **tie-out line**: *"✓ Σ CV_r + unexplained residual = CV, to the AED"* with the residual amount.

The demo is the two contrasting cases the spec called for: one **cost-contributor-driven** (a resource
dominates CV) and one **schedule-driven** (SV off, CV small).

### B. The API (for integration / scripting)

```
GET /api/v1/variance?bcc={id}&period={p}
Headers: X-User-Id, X-Project-Slug
```

Returns the full bridge DTO (both lanes, contributions, dominant, residual, tie-out flag, assumption
badge, evidence-needed, notes). Uses the platform's strongest tenant sequence
(`ProjectResolver → RLS IsAuthorizedAsync probe → registry`), so a project-keyed snapshot cache hit is
still authorized per request. A missing / non-`EP-` / non-live / null-money row returns
**`available:false` (200)** — an honest empty state, not an error. Errors: `400` blank `bcc` · `401` no
identity · `403` not a member · `404` unknown project / no data.

### C. The QS Copilot (plain English)

The engine is wired into the copilot as the **`ExplainVariance`** tool
(`Core/Agent/QsAnalyticsTools.cs`): ask *"why is BCC-… over budget?"* and it returns the same two-lane
attribution — CV/SV, dominant resource, contributions, the assumption badge, and the evidence needed —
with its source rows. The tool description tells the model to present the named driver as a
**hypothesis**, never a proven price-vs-productivity cause. (See [QS Copilot](15-idea-4-implementation.md).)

## Honest limits

- **Attribution, not diagnosis.** It names the dominant resource **contributor** (or the unexplained
  residual); the cause (price vs productivity) is a labelled hypothesis — this data has no labour hours
  or per-resource rates.
- **CV has no quantity term.** The engine never splits CV into quantity vs rate — that lives in the SV
  lane, shown separately. Selling a quantity split of CV would be a catchable math error.
- **Assumption-based allocation.** The EV→resource split uses estimate shares; the attribution depends
  on that assumption, flagged on every card.
- **Live `EP-` packages only.** Missing / non-`EP-` / zero-EV / null-money rows are `available:false`.
- **Single project, one owner.** The resource mix exists only for the estimate's owning project;
  elsewhere the bridge is CV/SV totals only.

## Where to look in the code

| Concern | File |
|---------|------|
| The deterministic engine (two lanes, tie-out, dominant rule, badges) | `Core/Variance/VarianceAttributor.cs` |
| Result records (`VarianceBridge`, `ResourceContribution`) | `Core/Variance/VarianceModels.cs` |
| Resource-mix aggregate (`Σ cost` by EP-package × resource) | `Core/Registry/ProjectSnapshotRegistry.cs` (`BuildResourceMix`) |
| HTTP endpoint (RLS-probed, `available:false` on non-diagnosable) | `Web.API/Controllers/VarianceController.cs`, `Web.API/Contracts/VarianceDtos.cs` |
| Copilot tool | `Core/Agent/QsAnalyticsTools.cs` (`ExplainVariance`) |
| UI (attribution line, badges, inline-SVG waterfall, tie-out) | `frontend/qs-early-warning/src/components/VarianceCard.tsx` |
| Watchlist click-through wiring | `frontend/qs-early-warning/src/App.tsx` (`onSelect` → `VarianceCard`) |
| Tests | `tests/QsEarlyWarning.Tests/VarianceTests.cs` |
</content>
</invoke>
