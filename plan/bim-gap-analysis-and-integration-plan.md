# BIM gap analysis — the CCI Pilot vs. QS Cost, and what's worth adopting

**Date:** 2026-07-29
**Subject solution:** `~/Downloads/tower-x-cost-intelligence-pilot` ("Continuous Construction Cost
Intelligence Pilot"), a separate hackathon entry by a colleague.
**Our solution:** `QsEarlyWarning/` (QS Cost — System of Record).

---

## 0. Executive summary

Their entry is a **React + Express/Prisma/SQLite document-intelligence product**: upload cost
documents, classify them, parse them, raise risk cases, brief a meeting. Ours is an **EVM analytics
system of record**: Postgres, a backtested early-warning classifier, a conformal forecaster, and a
13-tool copilot.

The two overlap in exactly one place — **BIM** — and there the comparison is uncomfortable in one
direction and reassuring in the other:

- **Their BIM plumbing is ahead of ours in three specific ways.** They persist a real IFC model
  server-side, they grade every model↔cost link by confidence, and they paint cost risk onto real
  IFC geometry. We do none of those three.
- **Their analytics are not close to ours.** They have **no Earned Value Management at all** — no
  CPI, no SPI, no PV, no EAC anywhere in the codebase. They store an `earnedValue` column and
  compute nothing from it. Their entire forecasting capability is one hard-coded rule:
  `if (forecastCost > budget) raise a risk`. There is no backtest, no baseline comparison, no ML.

So this is a **surgical adoption, not a catch-up**. Three of their ideas are worth taking, one is
worth adapting later, and several are worth explicitly rejecting — including one that would make
our take-off *worse* if ported faithfully (§4).

### "That company" — the answer

**That Open Company** (thatopen.com), the successor to IFC.js. Their code names it as a mandate:

> *"Real IFC → Fragments geometry conversion — **the mandatory BIM technology for this product**…
> exactly the workflow That Open's own docs recommend."*
> — `server/src/services/processing/ifcFragmentsConverter.ts:6`

> *"built on the mandatory That Open **Company** stack (`@thatopen/components`,
> `@thatopen/components-front`, `@thatopen/fragments`), never a handwritten geometry engine or
> generic Three.js boxes."*
> — `src/components/ifc/BackendIfcViewer.tsx:28`

**We already use the same stack.** `@thatopen/components@3.4.8` and `@thatopen/fragments@3.4.7` are
in our `frontend/qs-early-warning/package.json`; `model/viewer.ts` builds an `OBC.SimpleWorld` and
`model/ifcLoader.ts` uses `OBC.IfcLoader` with locally-served WASM. So That Open adoption is *not*
the gap. **The gap is what they do with it.**

---

## 1. What their solution actually is

| Aspect | Detail |
|---|---|
| Frontend | React 19 + Vite 8 + TypeScript, Tailwind v4, hash routing, Recharts |
| Backend | Express 4 + Prisma 6 + SQLite, port 4310, `tsx` (no build step) |
| Storage | Local disk `server/storage/<projectId>/<uuid>.<ext>` |
| Auth | **None** — no users, no tenancy, no companies (an explicit product decision, documented twice) |
| Tests | ~2,126 lines of server tests (vitest + supertest), 5 frontend test files |
| Docs | `PROTOTYPE_DECISIONS.md` (737 lines) — a genuinely good decision log |

**Pipeline:** upload → classify (filename rules + exact header-cell phrases + loose substrings,
with an ambiguity threshold) → six tabular processors (Cost Report, BOQ, Progress, Payment
Certificate, Variation, Procurement) → risk rules → cases → meeting brief.

**LLM use** is narrow but well-architected: a single `runAiOperation()` entry point, Anthropic +
OpenAI adapters behind one interface, credentials encrypted at rest with AES-256-GCM, every call
persisted with `inputRefs` rather than raw prompts. Only **two** operations are actually wired
(document classification, root-cause analysis); six more are defined but unused. No agent, no tool
loop, no MCP — and the chatbot was deliberately removed.

**Important framing caveat:** their "Tower X" is **not our dataset**. There is no
`Tower_X_Project_Data.xlsx` anywhere in their repo — their Tower X is 783 lines of hand-authored
TypeScript mock data (`src/data/mockData.ts`) that never touches their backend. Their real pipeline
was demoed with the public buildingSMART Duplex model instead. This matters when comparing claims.

---

## 2. Their BIM work — two entirely separate subsystems

This is the single most important structural fact about their solution.

### 2a. The mock layer — an "IFC-style prototype" with no IFC

- `src/data/buildingModel.ts` (504 lines) procedurally generates a 9-storey, **268-element**
  building from a structural grid. Every element gets a **fake but deterministic** IFC-shaped GUID
  via a hash function, plus a synthetic `expressId` starting at 91000.
- `src/data/ifc.ts` (189 lines) overlays risk/cost/progress on **only 19 of the 268 elements**.
- `src/lib/ifcScope.ts` aggregates financials across element/zone/floor/system/package scopes.
- Renders through React Three Fiber with a 2D fallback behind a visible banner.

They are scrupulous about naming it: `PROTOTYPE_DECISIONS.md:189` mandates the phrase *"interactive
3D IFC-style prototype"* and forbids "IFC engine" or "real IFC viewer".

**Verdict: not interesting to us.** Our massing is *derived from the BOQ* by `TowerSpecDeriver.cs`
— floors from the per-floor priced line, floor plate from soffit formwork ÷ floors, curtain-wall
area from the façade item — and every dimension ships with a `SourceItemRef` and a `Derivation`
string. Theirs is invented from a grid constant. Ours is strictly better founded.

### 2b. The real layer — the part worth studying

| File | What it does |
|---|---|
| `server/.../ifcFragmentsConverter.ts` (46 ln) | Real IFC→Fragments conversion **server-side in Node** via `@thatopen/fragments` `IfcImporter`, WASM resolved from `node_modules` (never a CDN). Convert once per upload, store the `.frag`, serve it to every session. Rejects non-`ISO-10303-21` content as a fast-fail. **Never falls back to generated geometry** — a conversion failure is surfaced honestly. |
| `server/.../ifcProcessor.ts` (299 ln) | A hand-written STEP/SPF parser for *metadata only*: 34-class allow-list, `IfcRelContainedInSpatialStructure` → storey name + elevation, property sets, and `IfcElementQuantity` take-off. Explicitly refuses to approximate geometry from `IfcExtrudedAreaSolid`. |
| `server/.../pipeline.ts` `linkElementAgainstRiskSet()` | **The best idea in their repo** — see §3.1. |
| `src/components/ifc/BackendIfcViewer.tsx` (564 ln) | Paints risk on *real* geometry: `getLocalIdsByGuids()` → `highlighter.styles.set()` → `highlightByID()`, grouped by worst Action Priority. Storey-grouped element browser; info panel showing GlobalId, psets, and each link's quality. |
| Prisma schema | `IfcElement` (globalId, expressId, ifcType, storeyName, storeyElevation, propertySets, quantities) and `IfcElementLink` (linkMethod, linkQuality, confidence, confirmed). |
| 3 API endpoints | Multi-model listing, state machine (`no_model`/`processing`/`failed`/`ready`), `.frag` streaming, and *"keep showing the last successfully-parsed version until a replacement succeeds"*. |
| `samples/ifc/` | The real 2.4 MB buildingSMART Duplex model, plus a script that generates a Cost Report whose `Storey` values match the model's real storeys **so the linking rule lights up real geometry** in the demo. |
| `server/tests/` | ~435 lines of IFC tests over real inline IFC4 STEP fixtures, including one asserting the parser *"never fabricates geometry"*. |

---

## 3. What they have that we don't — ranked by value to us

### 3.1 A graded element↔cost link with confidence and provenance ⭐ the best idea

Their linking rule, `pipeline.ts:84-107`, in full:

```ts
if (risk.costCode && propertyValues.has(risk.costCode.toLowerCase())) {
  // linkMethod 'deterministic-cost-code', linkQuality 'Direct Data', confidence 0.9
} else if (risk.location && el.storeyName &&
           risk.location.toLowerCase() === el.storeyName.toLowerCase()) {
  // linkMethod 'deterministic-storey-package', linkQuality 'Grouped Data', confidence 0.4
}
```

Two tiers, deterministic only, never AI. `propertyValues` is every property-set value on the
element, flattened and lower-cased — so if the model carries a cost code *anywhere* in its psets,
it links directly at 0.9. Otherwise it falls back to "same storey" at 0.4, and the UI says so
plainly: *"No direct cost link — showing grouped context."*

They also built the mirror function, `relinkExistingElementsToRisks()`, which re-runs linking
whenever the risk rule fires — because a project that uploads its IFC and its Cost Report in
separate batches would otherwise never connect the two. Their comment records that they found this
bug *"while preparing a hackathon demo."*

**Our equivalent is binary.** `model/ifcZoneMap.ts` applies 14 class+storey `ZONE_RULES`; an element
either lands in a zone or it doesn't, and the whole thing collapses to one `matchRate`. A slab
placed by "suspended floor slab" and a slab that literally carries the cost code `FLOORS-ALL` in a
property set are treated as identically confident. They aren't.

### 3.2 Cost risk painted onto the real IFC

Their `BackendIfcViewer` groups linked elements by worst Action Priority, resolves GUIDs→localIds,
and highlights.

**We have both halves and never joined them:**
- `components/ModelView.tsx` + `model/costPaint.ts` — paints money, on **generated massing**.
- `components/IfcTakeoff.tsx` + `model/ifcLoader.ts` — loads **real IFC**, and never colours it by
  cost. It has no period awareness at all.

The irony is that our own code already knows this is possible. `model/ifcLoader.ts:42`:

> *"Unlike the generated massing, a real IFC arrives with its own item index, so everything the
> generated path could not do — `getLocalIds`, `getItemsData`, `getBoxes`, **per-item colour** —
> works here."*

We wrote that, and then didn't use it for colour.

### 3.3 A persisted element index, and parse-once

They store elements in the DB and the converted `.frag` on disk. We re-parse an **8.6 MB IFC in the
browser on every single page load** and throw the resulting index away when the tab unmounts.

Consequences: the take-off is slow to open, nothing server-side can ever join a model element to
cost, and **the copilot can never see a model element** — `LocateCostRisk` works on zone rollups
from the panel, not on geometry.

### 3.4 IFC as a versioned, uploadable source

Per-file parse status, `keep-last-good-version`, and **multiple concurrent models per project**
(structure + MEP as separate uploads). Ours is one bundled file plus an ephemeral file picker.

### 3.5 Discovering cost codes inside property sets

Covered in 3.1, but worth separating because it is the cheapest single win. It is what turns *"an
element of this kind would map here"* into *"this element **is** that cost code."*

Our `flattenPsets()` (`ifcMeasure.ts:210`) currently cannot do this at all — it coerces every
property value with `Number.isFinite()` and **discards every non-numeric one**. A cost code in a
property set is thrown away before anything can see it.

### 3.6 `ifcGlobalId` on cost lines

Their `BoqLine`, `VariationLine` and `ProcurementLine` each carry an `ifcGlobalId` column. It is
parsed and stored and **consumed by nothing** — a gap in their own solution. But the *idea* — that
imported cost data can carry a model GUID directly — is sound and is the endgame for §3.1.

---

## 4. What we have that they don't — so we don't over-correct

**All of EVM.** `db/migrations/0003_evm_view.sql` computes CV/CPI/SPI/EAC/VAC as a real view.
They have none of it. Their `Earned Value` exists only as a column alias.

**A validated early-warning capability.** `RuleRiskScore@v1`, rolling-origin backtested over 8
folds and 117 GREEN→AMBER transitions, precision@5 of 45% against a 35% best CPI-native baseline.
Plus a ridge + cross-fitted split-conformal forecaster that beats four declared baselines. Their
equivalent is `score = min(100, round(overPct * 2))`.

**A take-off that survives real exporter output — and this is the important one.**

Their parser reads quantities *only* from `IfcElementQuantity`. Our sample model — a genuine
Autodesk Revit 2024 → IFC4 export — contains **zero** of those. The quantities exist only inside
Revit's own parameter groups, and on that file those groups are **in Spanish** (`Volumen`, `Área`).
Our `ifcMeasure.ts` handles this with a synonym table and reports `baseQuantitiesEmpty` so nobody
mistakes a pset scrape for a certified quantity.

> **Ported faithfully, their parser returns nothing on our data.** Their approach is the textbook
> one; ours is the one that works on real exporter output. This is not a gap — it is a thing we got
> right that they did not have to face, because their demo model (the buildingSMART Duplex) is a
> curated reference file that *does* carry BaseQuantities.

**Also ours alone:** rate-book pricing with a unit guard (refuses to price m³ at an m² rate) and a
tie-out that can actually fail; a zone cost map with a 1% materiality floor and an explicit
`unmappedBac` residual; Postgres with RLS; a 13-tool copilot with 21 deterministic offline evals;
the peer/zone collinearity experiment.

**Neither solution has any contractor or vendor dimension.** I checked both exhaustively. In ours,
`SUBCONTRACT` is a resource *type*, not a counterparty; in theirs, `mainContractor` is a free-text
field on project setup that is never even persisted to the backend. So this is a genuine gap in the
category — but **not something to port**, because there is nothing there to port.

---

## 5. Verdict on each feature

| Their feature | Verdict | Why |
|---|---|---|
| Graded link quality + confidence | **Adopt** — Phase 2 | Best idea in their repo; fits our honesty culture exactly |
| Paint cost on real IFC geometry | **Adopt** — Phase 1 | Highest demo value; we already have both halves |
| Pset cost-code discovery | **Adopt** — Phase 2 | Cheapest win; one function change unblocks it |
| Persist `.frag` + element index | **Adapt, defer** — Phase 3 | Right direction, wrong runtime for us — see Phase 3 |
| IFC as versioned upload source | **Defer** | Belongs in our existing import / Data Admin flow, post-hackathon |
| Server-side Node `IfcImporter` | **Reject** | Our backend is C#; would mean a second runtime to deploy and demo |
| Their STEP metadata parser | **Reject** | Returns zero quantities on our real Revit export (§4) |
| Their 268-element mock building | **Reject** | Ours is BOQ-derived with provenance; theirs is invented |
| Their AI provider abstraction | **Reject** for now | We have a working MAF path; not a differentiator here |
| Their `ifcGlobalId` cost columns | **Note only** | Unused even in their own code; it's the endgame for Phase 2 |

---

## 6. The build plan

Scope: the surgical subset. All in the frontend, no backend or schema change.

> **Status: Phases 1 and 2 are built and verified** (2026-07-29). Phase 3 remains deferred by
> design. What the tab reports on the bundled model is recorded in §7.

### Step 0 — resolve the highlight API ✅ resolved

Their viewer uses `OBF.Highlighter` from `@thatopen/components-front`, which we do not have.
`FragmentsModel` exposes `setColor` / `setOpacity` / `resetColor` / `resetOpacity` natively
(`@thatopen/fragments/dist/index.d.ts:5505-5510`), so **no new dependency was added**.

### Phase 1 — paint real cost risk onto the real IFC

1. **`model/ifcMeasure.ts`** — `ClassMeasurement.byStorey` is `Record<string, number>` (counts
   only), so localIds are discarded at line 107. Add `idsByStorey: Record<string, number[]>` and
   derive the existing `byStorey` counts from it, leaving the current shape intact for callers.
2. **`model/ifcZoneMap.ts`** — add `localIds: number[]` to `ZoneMatch`, accumulating in the loop at
   lines 97–108. **Preserve the per-(class, storey) rule application exactly** — its docblock
   records a real bug where testing storey per-class made the below-ground rule fire for all 299
   slabs and reported a flattering 100% match rate.
3. **New `model/ifcPaint.ts`** — group localIds by colour and apply. **Reuse `colorFor()` and
   `legendFor()` from `model/costPaint.ts`** rather than restating colour policy, so the IFC tab and
   the massing tab can never disagree about what AMBER means.
4. **`components/IfcTakeoff.tsx`** — add a period scrubber; fetch the zone cost map through the
   existing `api/client.ts` call that `ModelView.tsx` already uses; paint; render the shared legend.
   Extend the existing *"This is not Tower X"* banner to state that the colours are Tower X zone
   cost applied to a mapped school model — a mechanism demo, never that building's budget.

### Phase 2 — the graded element↔cost link

1. **`model/ifcMeasure.ts`** — split `flattenPsets()` (line 210) into the existing numeric flattener
   plus a string flattener returning lower-cased values. This is their `propertyValues` trick and it
   is what makes tier `Direct` possible at all.
2. **New `model/ifcCostLink.ts`** — three tiers:
   - **`Direct`** (0.9) — a pset value matches a known cost-centre code or BOQ item ref.
   - **`Grouped`** (0.4) — the existing `ZONE_RULES` class+storey match.
   - **`None`** — unmatched, and stays visibly unmatched.

   Source the code list from the existing cost-centre endpoint; do not hard-code it.
3. **Report a link-quality breakdown** in `IfcTakeoff.tsx` — *"12% Direct / 46% Grouped / 42%
   None"* — in place of the single match rate. Keep `matchRate = Direct + Grouped` so slide 14's
   58% stays comparable.
4. **Paint honours confidence** — Direct at full colour, Grouped at reduced opacity, None as a grey
   ghost. This is the visual form of their Direct/Grouped hierarchy, and it makes a weak link *look*
   weak instead of looking like a verdict.
5. **First frontend tests.** `package.json` has no test script and ~1,900 lines of 3D/IFC TypeScript
   have zero coverage. Add vitest; cover `mapToZones` (including the storey regression its own
   docblock describes) and the new link tiering.

### Phase 3 — persistence (decided, deliberately not built now)

**Architecture: the browser converts, the API stores.** The client already loads and measures the
IFC; POST the resulting `.frag` bytes plus the element index to the .NET API — migration
`0011_ifc_model.sql` → `ifc_models` / `ifc_elements` / `ifc_element_links`, plus a new
`IfcController`.

This gets their parse-once-reuse-forever benefit **with no second runtime.**
`@thatopen/fragments`' `IfcImporter` is Node-only and has no .NET equivalent, so mirroring their
server-side design literally would mean standing up and deploying a Node sidecar purely to convert
files. Not worth it for a hackathon, and the client-side path produces an identical `.frag`.

Once this lands, the copilot can finally see model elements and `LocateCostRisk` can answer *"which
elements"* rather than *"which zone."*

### An idea neither team has built

Both solutions measure model quantities, and **neither compares them to the BOQ.** We compute
2,735.5 m³ of slab off the model; the BOQ priced some other number. That difference is a
quantity-growth early warning available *before a single invoice is raised* — the earliest signal in
the whole problem statement. Our `RateBook` + `TakeoffPricer` are already 90% of the machinery.
Worth considering after Phase 2.

---

## 7. Verification — results

**This project uses pnpm, not npm.** `npm install` fails outright on the `@thatopen/fragments`
dependency tree (`Cannot read properties of null (reading 'matches')`); `pnpm` is the only
package manager that resolves it.

| Check | Result |
|---|---|
| `dotnet test` — main suite | **136 / 136 pass** |
| `TakeoffPricingTests` + `SpatialCostMapTests` | **19 / 19 pass**, measured constants unchanged |
| `pnpm test` — new vitest suite | **14 / 14 pass** (6 zone-map, 8 cost-link) |
| `pnpm build` | passes; `IfcTakeoff` still lazy-split (22.8 kB, viewer chunk separate) |
| **IFC Take-off** in Chrome | model loads from local WASM, paints, scrubber recolours, legend shared with `ModelView` |
| **3D Cost X-Ray** | tie-out still holds: `✓ Zones + unmapped = 224,322,886 AED` |

`QsEarlyWarning.Db.Tests` (2 tests) fail with *"Docker is either not running or misconfigured"* —
they are Testcontainers-backed Postgres tests and need Docker running. Unrelated to this work.

**Headless browsers cannot verify this tab.** wstack `browse` and any SwiftShader-backed Chromium
fail with `THREE.WebGLRenderer: Error creating WebGL context`, so the viewer never initialises and
none of the cards render. Verification must use real Chrome with GPU access.

### What the bundled model actually reports

| Metric | Value |
|---|---|
| Elements in measured classes | 1,526 |
| Measurable | 58% (883) — unchanged |
| Tie-out | ✓ 508 priced + 375 unpriced + 643 unmeasured = 1,526 |
| Zones reached | 3 of 10 — STRUCTURE 1,225 · FLOORS-ALL 299 · EXTERNAL-FACADE 2 |
| **Direct links** | **0 (0%)** |
| **Grouped links** | **1,526 (100%)** |
| None | 0 |

**Zero Direct links is the honest headline, not a bug.** Not one element in a real Autodesk Revit
structural export carries a cost code in its property sets, so every placement is a rule's inference
about a category. The tab says so in words and draws every element at 0.55 opacity to say it
visually. That is the ceiling a QS should know about before trusting any model-driven cost figure.

The storey regression the `ifcZoneMap` docblock records was also confirmed fixed **on real data**,
not just in the unit test: the model does contain a `Sub Level`, and BASEMENT correctly receives
**zero** slabs — all 299 are placed on the levels they actually sit on.

---

## Appendix — drift found while comparing (all verified, all cheap, not in the plan above)

| Claim | Reality |
|---|---|
| Deck slide 11: "12 read-only tools" | **13** registered in `ClaudeQsCostCopilotAgent.cs` |
| Deck slide 16: "12 tabs" | **14** in `App.tsx` |
| Deck slide 16: "9 migrations" | **10** (`0010_zones.sql`) |
| Deck slide 16: "~100 xUnit tests" | **~137** |

- **`LocateCostRisk` is absent from `CopilotPrompts.System`** (verified: zero occurrences). It is
  registered as a tool but the routing guidance names the other 12, so the model can only find it
  via its `[Description]` attribute. One-line fix, meaningful demo risk.
- Deck slides 13/14 AED figures (19.96M, 8.1M, 43.5M, 4.64M) are **hard-coded HTML**, not live.
  The element counts (375 / 619 / 203) are corroborated by `TakeoffPricingTests`; the money is not
  asserted anywhere. Re-verify before presenting.
- **A live Anthropic API key sits in plaintext** in `src/QsEarlyWarning.Web.API/appsettings.Development.json`.
  It is correctly gitignored and confirmed absent from git history — but it is on disk. Rotate it
  before sharing the repo or screen-sharing.
- `data/card-download-report-2026-07-28.numbers` is untracked and unrelated to the product.
