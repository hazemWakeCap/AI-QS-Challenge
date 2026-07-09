# Plan: Enhance Data Admin UX — make it read like the QS's workbook

> On approval, this plan is saved to `/Users/hazem/hackathon/AI-QS-Challenge/plan/` (already there).
> **Suggested execution order (matches the house workflow in `enhance-dashboard-ux.md`):**
> (1) save plan → (2) `/loop` a Codex agent to review this plan and fill the **## Codex Review** section →
> (3) address findings in-plan → (4) Codex re-review → (5) implement.

## Context — why the dropdown feels wrong

The **Data Admin** tab (`DataAdmin.tsx`, feature 10) renders a flat `<select>` whose options come
straight from the server **entity registry** (`EntitiesController.Registry()` → `EntityRegistry.All`,
`QsEarlyWarning.Infrastructure/Crud/EntityRegistry.cs`). That registry is a **normalized relational
schema of 14 tables**, so the dropdown shows 14 DB-flavoured names:

`Estimate Versions · Norms · Norm Materials · Estimate Packages · BOQ Items · BOQ → Norm Mappings ·
Estimate Resource Lines · Cost Centres · Cost Centre Baselines · Plan Curve (per period) ·
Reporting Periods · Cost Centre Periods (facts) · Cost Ledger (deltas) · Import Runs`

But the QS's mental model is the **workbook** — the 5 sheets they actually opened
(`data/Tower_X_Project_Data.xlsx`): `1_BOQ`, `2_ESTIMATE_NORMS`, `3_BOQ_MAPPING`,
`4_ESTIMATE_DATASHEET`, `9_HISTORICAL_DATA`. So there's a **mental-model mismatch**, made worse by:

- **No grouping** — 14 peer options in one alphabet-ish list; the 6 tables from the historical/EVM
  (`9_HISTORICAL_DATA`) side dominate and bury the 4 estimate sheets.
- **No lineage** — nothing tells the user "this table came from *2_ESTIMATE_NORMS*"; the normalization
  (one sheet → several tables) is invisible, so the extra rows read as noise, not structure.
- **DB vocabulary** — "Cost Ledger (deltas)", "Plan Curve (per period)", "facts" are engine terms, not
  QS terms; no plain-language "what is this / what's the grain" hint like `DATA_DICTIONARY.md` gives.
- **No read-only signal up front** — 3 tables are import/procedure-managed (read-only); the user only
  learns that after selecting and seeing the "· read-only" text at the far right.

The structure is *correct* (it's a proper relational store with FKs, tenancy, versioning — see the
earlier analysis). The fix is **presentation only**: reshape Data Admin so the QS navigates by the
workbook they know, with the normalized tables grouped and labelled underneath.

**Goal:** the QS opens Data Admin and sees *their sheets first*; each sheet expands to the underlying
table(s) with a one-line "what this holds" blurb and a lineage breadcrumb. No schema change, no loss of
any table, no change to the CRUD behaviour or the DB-enforced invariants.

---

## The canonical mapping (the core artifact)

Sheets **1–4 map 1:1** to the QS's estimate workbook tabs. Sheet **9_HISTORICAL_DATA** is the
cost-centre × period source (BAC/PV/EV/AC…) that `WorkbookImporter` reads: the importer directly INSERTs
its **source inputs** — cost centres, BAC baselines, the planned S-curve, reporting periods, and the
per-period actual facts — into `cost_centres` / `cost_centre_baselines` / `cost_centre_plan_periods` /
`reporting_periods` / `cost_centre_periods`. So those tables **are imported from `9_HISTORICAL_DATA`**.
What is *not* imported is the **computed EVM** (CPI/SPI/EAC/VAC/alerts): per `CLAUDE.md` those computed
output sheets are intentionally excluded from the workbook, so the runtime **derives** them from the
imported inputs (the `cost_centre_evm` view). One table, `cost-deltas` (`period_cost_deltas`), is **not**
importer-loaded at all — it is populated by the monthly capture / ledger flow (`sp_post_cost_delta` /
cutover). Two tables (`estimate-versions`, `import-runs`) are engine/audit infrastructure with no sheet.

| Group (nav) | Sheet ref shown to QS | Grain / blurb | Registry entities (keys) |
|---|---|---|---|
| **Bill of Quantities** | `1_BOQ` | One priced work item | `boq-items` |
| **Estimate Norms** | `2_ESTIMATE_NORMS` | An estimating recipe + its materials | `norms`, `norm-materials` |
| **BOQ Mapping** | `3_BOQ_MAPPING` | BOQ line → norm → estimate package | `boq-mappings`, `estimate-packages` |
| **Estimate Datasheet** | `4_ESTIMATE_DATASHEET` | BOQ item exploded into resource lines (unit rates) | `resource-lines` |
| **Cost Centres & Budget** | `9_HISTORICAL_DATA` | Cost centre, its BAC baseline, planned S-curve — imported source inputs | `cost-centres`, `baselines`, `plan-periods` |
| **Periods & Actuals** | per-entity: `9_HISTORICAL_DATA` for the imported tables; **none** (live capture) for `cost-deltas` | Reporting periods + per-period actual facts (imported); cost ledger captured live | `reporting-periods`, `cost-centre-periods`, `cost-deltas` |
| **System & Import** | — (engine) | Estimate versioning + import audit log | `estimate-versions`, `import-runs` |

> **Honesty note (carry into Codex review):** the sheet-9 label must be provenance-accurate. The
> **source inputs** (cost centres, BAC baselines, plan curve, reporting periods, actual facts) **are
> imported directly from `9_HISTORICAL_DATA`** by `WorkbookImporter` — do NOT label them "derived, not
> imported". What is *derived* is the **computed EVM** (CPI/SPI/EAC/VAC/alerts), which `CLAUDE.md` says
> is intentionally left out of the workbook and computed at runtime. The one genuinely non-imported
> table is `cost-deltas`, populated by the live monthly-capture ledger, not by the importer. Getting
> this wording wrong (either direction) would mislead the QS about provenance — the exact thing this
> feature is meant to fix.

---

## Scope decisions (proposed — confirm or override)

- **Server-driven grouping (recommended)** over client-side hardcoding. The whole Data Admin philosophy
  is "the registry is the single source of truth; the UI auto-generates from it" (feature-10 doc). So the
  grouping belongs in `EntityRegistry`, not as a magic map in the React file. *(Alternative: a client-side
  `SHEET_GROUPS` const in `DataAdmin.tsx` — faster, zero backend, but drifts from the registry the moment a
  table is added/renamed. Chosen against, but noted for a time-boxed version.)*
- **Two-level "workbook" nav**, not a fancier IA. Primary = the 7 groups above styled as **workbook
  sheet-tabs** (evoking Excel's bottom tabs); secondary = table chips only when a group has >1 table.
- **Frontend stays plain React + one `styles.css`** — reuse existing tokens/classes (`.pill`, `.tab`,
  `.grid`, `.card`, `--sp-*`, `--r-*`). No UI lib, consistent with `enhance-dashboard-ux.md`.
- **Out of scope:** any change to CRUD semantics, SQL, tenancy, or the generic service; new endpoints
  beyond enriching the existing `GET /api/v1/entities` payload; renaming DB tables/columns; light theme.

---

## The work

### 1. Registry metadata — `EntityRegistry.cs` (backend, the foundation)
Add grouping/lineage metadata to `EntityDescriptor` so the UI can build the workbook nav from the server.
**Nav order is fully determined by two fields — `GroupOrder` (between groups) + `Order` (within a group) —
never by registry array position** (the array starts with `estimate-versions`, `EntityRegistry.cs:29`, so
first-seen order would wrongly float **System & Import** to the front; see Codex Round 1 #1/#2):
- Extend the record with two kinds of metadata. **Group-level (shared within a group, drives nav):**
  `string Group` (stable key, e.g. `"boq"`, `"norms"`, `"mapping"`, `"datasheet"`, `"cost-centres"`,
  `"periods"`, `"system"`), `string GroupLabel` (display, e.g. `"Bill of Quantities"`), `int GroupOrder`
  (**sort BETWEEN groups** — the primary nav order), and `int Order` (**sort WITHIN a group**).
  **Entity-level (may differ per entity, even within a group — drives lineage):** `string? SheetRef`
  (e.g. `"1_BOQ"`; `null` when the entity has no originating sheet — System infra, and `cost-deltas`
  which is captured live, not imported) and `string Blurb` (one-line "what this holds / grain", sourced
  from `DATA_DICTIONARY.md` / `CLAUDE.md`).
- Assign `GroupOrder` so the primary nav is deterministically **BOQ (0) → Norms (1) → Mapping (2) →
  Datasheet (3) → Cost Centres & Budget (4) → Periods & Actuals (5) → System & Import (6)**. Every entity
  sharing a `Group` MUST carry the same `GroupLabel`, `GroupOrder`, and `Group` — the nav-ordering fields
  (asserted in tests — see Verification). `SheetRef` and `Blurb` are **decoupled from grouping**: they are
  per-entity, so members of one group can carry different lineage (e.g. within the `periods` group,
  `reporting-periods`/`cost-centre-periods` carry `SheetRef = "9_HISTORICAL_DATA"` while `cost-deltas`
  carries a distinct lineage — see §3 and the Honesty note). Together, `GroupOrder` then `Order` give a
  total ordering over all 14 entities.
- Populate all 14 entries per the mapping table above. Keep the additions as **defaulted trailing
  parameters** (or a nested `GroupInfo` record) so the 14 existing `new(...)` call-sites stay compilable
  with minimal churn.
- Extend the `Registry()` projection in `EntitiesController.cs:30-37` to emit `group`, `groupLabel`,
  `groupOrder`, `sheetRef`, `blurb`, `order` alongside the current fields. **No new endpoint.**

### 2. API types — `api/client.ts`
Extend `EntityMeta` (`client.ts:65`) with `group: string; groupLabel: string; groupOrder: number;
sheetRef: string | null; blurb: string; order: number;`. Purely additive; no call-site changes elsewhere.

### 3. Reshape the Data Admin nav — `DataAdmin.tsx`
Replace the single flat `<select>` (`DataAdmin.tsx:101-103`) with the two-level workbook nav:
- **Derive groups** from `metas`: `groupBy(m.group)`, **ordered by `groupOrder`** (NOT first-seen array
  order — the registry array starts with `estimate-versions`, so first-seen would make System & Import the
  first tab; Codex Round 1 #1). Within each group sort tables by `order`. The default-selected table on load
  **replaces** the old `m[0]?.key` fallback (`:20`, which picked the array's first entry = `estimate-versions`):
  compute the ordered groups first, then default to the **first table (lowest `order`) of the first group
  (lowest `groupOrder`)** — i.e. `boq-items` under Bill of Quantities, so the QS lands on their sheets.
- **Primary: sheet-tabs.** A row of tabs, one per group, each showing `GroupLabel` with a small monospace
  sheet-code eyebrow (e.g. `1_BOQ`). Since `SheetRef` is now per-entity, the tab derives its eyebrow from
  the group's **representative entity** = its first-ordered member (lowest `Order`); for every sheet-backed
  group that member's `SheetRef` is the group's real sheet (e.g. `periods` → `reporting-periods` →
  `9_HISTORICAL_DATA`), and the per-entity split (`cost-deltas`' distinct lineage) surfaces in the sheet
  **header** on selection, not on the group tab. Reuse `.tab`/`.tab.active` styling; add a `.sheet-tabs`
  wrapper. Clicking a group selects its first (default) table.
  - **Dimming rule (concrete).** The System group is the only "no sheet" group: it has `group === "system"`
    and its representative entity's `sheetRef === null`. Render its tab with an extra `.sheet-tab-system`
    class (equivalently keyed off the representative `sheetRef === null` so any future sheetless group
    inherits the treatment — note this is the **group representative's** sheetRef, so `periods` is NOT
    dimmed even though its `cost-deltas` member has a null sheetRef). §4 defines that class as a
    muted/reduced-opacity variant. It already sorts **last** via `GroupOrder` 6 and is never selected on load
    (default is `boq-items`), so "dimmed + last" needs no extra ordering logic — just the class.
- **Secondary: table chips.** Precise rule (Codex Round 1 #3): render the chip row **whenever the selected
  group has >1 table**, otherwise hide it. Under this rule the single-table groups **BOQ (`boq-items`) and
  Datasheet (`resource-lines`)** show no chips, while **System & Import DOES get chips** (it maps two
  entities — `estimate-versions`, `import-runs`). This is consistent with Open Question #2's resolved
  default (show System as the last tab, dimmed) — a dimmed 2-table group still renders its chips.
- **Sheet header / lineage.** Above the grid show a breadcrumb built from the **selected entity's own**
  `SheetRef` (per-entity, not the group's): when `SheetRef` is non-null, render `SheetRef ▸ table Display`
  (e.g. `9_HISTORICAL_DATA ▸ Reporting Periods`); when `SheetRef` is `null`, drop the sheet crumb and
  show a lineage label sourced from the `Blurb` instead (e.g. `cost-deltas` → *"captured live via the
  monthly cost-ledger flow — not imported"*, System infra → *"engine/audit — no sheet"*). This is what
  keeps `cost-deltas` from displaying the misleading `9_HISTORICAL_DATA` crumb its group-mates carry.
  Then show the `Blurb` as muted sub-text, the row count, and a `read-only` pill when the entity has **no
  mutating capability**:
  `!meta.caps.create && !meta.caps.update && !meta.caps.delete` (Codex Round 1 #4 — plain `!caps.create` is
  too loose; an entity can be non-creatable yet still editable/deletable). `EntityCaps` (`client.ts:64`)
  already exposes `create`/`update`/`delete`, so no type change is needed. This replaces the easily missed
  inline "· read-only" text at `:105`. Keep the existing `+ Add` button (still gated on `meta.caps.create`),
  now in this header.
- Everything else in `DataAdmin.tsx` (grid render, FK option loading `:23-40`, add/edit form `:111-141`,
  save/delete) is **unchanged** — this is a nav/header reskin around the existing body.

### 4. Small delight: Excel-style sheet tabs — `styles.css`
Add a `.sheet-tabs` treatment that reads like a workbook: bottom-anchored tab shapes, active tab lifted
with the accent, monospace sheet-code eyebrow (`.sheet-tab .code`), horizontal scroll on ≤760px (mirror
the existing `.tabs` mobile rule at `styles.css:280-283`). Add `.data-admin-head` (breadcrumb + blurb +
count + read-only pill) and a `.chip-row` for the secondary table chips. Reuse tokens only; no new colors
beyond the existing `--accent`/`--muted`/`--warn`.
- **`.sheet-tab-system` (dimming CSS, concrete).** Define the sheetless System tab as muted: `opacity: 0.6`
  and `color: var(--muted)` (no new token — reuse the existing `--muted`). When it becomes the active tab
  (user clicks it) it still reads as selected via `.tab.active`, so scope the dim to the non-active state
  (e.g. `.sheet-tab-system:not(.active)`). This is the only visual difference from a normal sheet-tab.

### 5. Copy pass — blurbs & labels
Write the 14 one-line `Blurb`s in QS language, pulled from `DATA_DICTIONARY.md`/`CLAUDE.md` (e.g.
resource-lines → *"BOQ item exploded into resource lines — unit rates live here"*; cost-deltas →
*"Per-period cost movements captured to the ledger (read-only)"*). Keep the existing DB `Display` names as
the table title, but the sheet-first framing + blurb gives the plain-language anchor the QS needs.

### Representative files
- **Backend:** `src/QsEarlyWarning.Infrastructure/Crud/EntityRegistry.cs` (record + 14 entries),
  `src/QsEarlyWarning.Web.API/Controllers/EntitiesController.cs` (Registry projection, ~`:30-37`).
- **Frontend:** `src/api/client.ts` (`EntityMeta` type), `src/components/DataAdmin.tsx` (nav + header),
  `src/styles.css` (`.sheet-tabs`, `.data-admin-head`, `.chip-row`, mobile rule).
- **Tests/docs:** **Add** `tests/QsEarlyWarning.Tests/EntityRegistryShapeTests.cs` — no registry-shape test
  exists today (that project already references `QsEarlyWarning.Infrastructure`, so `EntityRegistry.All` is
  in scope; use xunit, matching the sibling tests). The test asserts, over `EntityRegistry.All`:
  (a) all 14 entities have non-empty `Group`/`GroupLabel` and a non-null `GroupOrder`; (b) exactly **7**
  distinct groups; (c) `GroupOrder` order equals the mapping table (BOQ→Norms→Mapping→Datasheet→Cost
  Centres & Budget→Periods & Actuals→System & Import); (d) all entities sharing a `Group` carry an identical
  `GroupLabel` **and** `GroupOrder` (the shared **nav-ordering** metadata only — `SheetRef`/`Blurb` are
  per-entity and are deliberately **not** required to match within a group); (e) provenance is
  per-entity and accurate: the imported sheet-9 tables (`reporting-periods`, `cost-centre-periods`,
  `cost-centres`, `baselines`, `plan-periods`) carry `SheetRef == "9_HISTORICAL_DATA"`, while
  `cost-deltas` — though in the `periods` group — has `SheetRef != "9_HISTORICAL_DATA"` (it is `null`,
  live-capture) and its `Blurb` is **not** mislabeled as sheet-9-imported; and no `Blurb` claims the
  sheet-9 source inputs are "derived, not imported" (only the computed EVM is derived, per the Honesty
  note). Also `docs/10-data-administration.md` (document the workbook grouping + the
  imported-inputs-vs-derived-EVM provenance nuance, and that lineage is per-entity so a group can mix
  imported and live-capture tables).

---

## Decisions (defaults taken)
These are **decided defaults**, not open blockers — implement to them. Each is still overridable by the user
at the approval gate; absent an override, build exactly what's stated here.
1. **Sheet-9 wording — DECIDED (provenance-accurate).** The two sheet-9 groups are labelled *"Cost Centres
   & Budget"* and *"Periods & Actuals"*, each subtitled with the real source **`9_HISTORICAL_DATA`** because
   `WorkbookImporter` imports their source inputs (cost centres, BAC baselines, plan curve, reporting
   periods, actual facts) directly from that sheet. What the copy must NOT claim is that these tables are
   "derived, not imported" — only the **computed EVM** (CPI/SPI/EAC/VAC/alerts) is derived at runtime (the
   excluded output sheets per `CLAUDE.md`), and the lone non-imported table is `cost-deltas` (live capture
   ledger). This is the one place a wrong word misleads on provenance, so the framing is fixed and asserted
   in the registry-shape test (Representative files, assertion (e)). *(Override at approval to retune the exact
   words.)*
2. **`System & Import` visibility — DECIDED.** Show it as the **last** sheet-tab (`groupOrder` 6), visually
   **dimmed** (mechanism specified in §3/§4), never selected on load. Because it maps two entities it **still
   renders its chip row** (`estimate-versions` / `import-runs`) per the >1-table chip rule in §3.
   *(Override at approval to hide it behind a collapse toggle instead.)*
3. **No flat "A–Z all tables" escape hatch — DECIDED.** The grouped workbook nav supersedes the old flat
   `<select>`; no toggle back to it and no `<optgroup>` fallback is built. *(Override at approval if a
   power-user flat view is wanted.)*

---

## Codex Review

_Automated Codex review loop (`/review_plan`). Rounds appended below; most recent last._

**VERDICT — READY FOR EXECUTION** (converged after 5 Codex rounds; no [P1] remaining). Round 5 was a
narrow confirmation of the Round 4 lineage/grouping decoupling: Codex verified consistency across §1,
the mapping table, §3, the registry-shape test spec, and Verification, and returned
_"VERDICT: READY — no [P1] findings."_

### Round 5 — READY (0 [P1]) · final convergence check
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| — | — | None. Confirmed the Round 4 fix is genuine and introduced no new contradiction; sheet-9 provenance, `/run_system`, ordering, read-only signal, and lineage decoupling all verified against the code. | Converged — plan is execution-ready. |

### Round 4 — CHANGES NEEDED (1 [P1]) · Round 3 fixes confirmed genuine
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | Internal contradiction introduced by the Round 1 + Round 3 fixes: the "every entity in a group shares identical `SheetRef`" invariant forces `cost-deltas` (in the `periods` group) to inherit the `9_HISTORICAL_DATA` breadcrumb/test — but the plan now correctly says `cost-deltas` is live-capture/ledger-managed, not importer-loaded. Allow an entity-level `SheetRef` exception (or a per-entity lineage label) for `cost-deltas`. | Confirmed & fixed. Decoupled lineage from grouping: `SheetRef`/`Blurb` are now **per-entity** while only `Group`/`GroupLabel`/`GroupOrder` stay shared (nav ordering preserved). Reconciled §1 (two-tier metadata + weakened invariant), the mapping-table `periods` row, §3 sheet-header (per-entity `SheetRef`; `null` → live-capture lineage label so `cost-deltas` no longer shows the sheet-9 crumb), registry-shape test (d)/(e) and Verification steps 2/5 (assert shared nav metadata only; imported sheet-9 tables `== 9_HISTORICAL_DATA`, `cost-deltas != it`), and annotated the stale Round 1 #2 clause. |

### Round 3 — CHANGES NEEDED (2 [P1]) · Round 1 + 2 fixes confirmed genuine
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | Sheet-9 provenance is **factually wrong**: `WorkbookImporter` directly loads `9_HISTORICAL_DATA` into `reporting_periods`/`cost_centres`/`baselines`/`plan_periods`/`cost_centre_periods` (`WorkbookImporter.cs:63`, `db/README.md:75`). Plan's "derived, not imported" label + the "not imported" test assertion are wrong. Correct nuance: source **inputs** imported from sheet 9; computed EVM **outputs** derived from them. | Confirmed & fixed. Verified `WorkbookImporter.cs` INSERTs sheet-9 inputs into `reporting_periods`(L64)/`cost_centres`(L83)/`cost_centre_baselines`(L91)/`cost_centre_plan_periods`(L98)/`cost_centre_periods`(L141); only `cost-deltas` (`period_cost_deltas`) is not importer-loaded (capture/ledger flow). Rewrote the mapping intro + both sheet-9 rows (`SheetRef` now `9_HISTORICAL_DATA`), the Honesty note, and Decisions #1 to "imported source inputs; only computed EVM (CPI/SPI/EAC/VAC/alerts) is derived per CLAUDE.md; cost-deltas is live-capture". **Flipped** the wrong assertions: registry-shape test (e) and Verification step 5 now assert the sheet-9 tables reference `9_HISTORICAL_DATA` and are NOT mislabeled "derived, not imported". |
| 2 | P1 | Verification misstates `/run_system`: it does **not** start the DB — it checks Postgres/`qs_phase1` exists and stops if not (`run_system.md:12`). Raw fallback omits the setup sequence create DB → `db/apply.sh qs_phase1` → importer (`db/README.md:80`); fresh machine gets stuck. | Confirmed & fixed. Verified `run_system.md:12-15` only *checks* `qs_phase1` and stops with guidance if absent. Verification step 1 now (a) describes `/run_system` as starting **API + dashboard** and *checking* (not provisioning) Postgres/`qs_phase1`, stopping if missing; and (b) prepends the one-time DB prerequisite from `db/README.md:80-84`: `CREATE DATABASE qs_phase1;` → `QsEarlyWarning/db/apply.sh qs_phase1` → `dotnet run --project QsEarlyWarning/tools/QsEarlyWarning.Importer -c Release`, before both the slash-command and raw-fallback run paths. |

### Round 2 — CHANGES NEEDED (1 [P1], 2 [P2]) · Round 1 fixes confirmed resolved
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | Verification references `/run_system` as mandatory but Codex found no such skill in repo docs; README uses `dotnet run` + `pnpm dev` — "non-executable as written". | Rebutted+hardened: `/run_system` exists at `.claude/commands/run_system.md` (Codex can't see it — outside its sandbox); Verification step 1 now notes it is a project command AND adds the raw `ASPNETCORE_ENVIRONMENT=Development dotnet run --project QsEarlyWarning/src/QsEarlyWarning.Web.API` + `pnpm install`/`pnpm dev` fallback (confirmed against `QsEarlyWarning/README.md`), so the step is executable either way. |
| 2 | P2 | Open questions still say "surface at approval gate" despite claiming resolved defaults; convert to decisions (sheet-9 wording, System visibility). | Fixed: renamed the section to **"Decisions (defaults taken)"** — each item now states the chosen default (sheet-9 *derived* wording, System shown last/dimmed, no A–Z escape hatch) as a decision to implement, with a parenthetical "override at approval" note instead of being a blocker. |
| 3 | P2 | Plan says the System & Import tab is "dimmed" but §3/§4 never specify the class/CSS rule for dimming. | Fixed: §3 adds a concrete dimming rule (the `group === "system"` / `sheetRef === null` tab gets a `.sheet-tab-system` class), and §4 defines that class's CSS (`opacity: 0.6` + `color: var(--muted)`, scoped to `:not(.active)`), still sorted last via `GroupOrder` 6. |

### Round 1 — CHANGES NEEDED (1 [P1], 5 [P2])
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | §3 "ordered by first-seen registry order" makes **System & Import** the first tab/default (registry starts with `estimate-versions`, `EntityRegistry.cs:29`) — contradicts "QS sees their sheets first". | Fixed: added `int GroupOrder` to `EntityDescriptor` in §1 with the explicit sequence BOQ(0)…System(6); §3 now derives groups by `groupOrder` (not first-seen) and defaults the selected table to `boq-items`. |
| 2 | P2 | §1 `Order` is intra-group only; no group-level ordering/metadata contract or assertions. | Fixed: §1 states `GroupOrder` (between groups) + `Order` (within group) give a total ordering, and that group members must share the nav-ordering metadata `GroupLabel`/`GroupOrder`; Verification step 2 + the new registry-shape test (Representative files, step 6) assert this. _(Round 4 #1 superseded the original "share `SheetRef`" clause: `SheetRef`/`Blurb` are now per-entity lineage, decoupled from grouping — only `Group`/`GroupLabel`/`GroupOrder` are shared.)_ |
| 3 | P2 | §3 "hide chips for 1:1 groups (…System-as-needed)" ambiguous — System maps 2 entities, so not 1:1. | Fixed: §3 chip rule rewritten to "chips whenever group has >1 table" — so BOQ/Datasheet hide, **System shows** chips; reconciled with Open Question #2 (System shown last, dimmed, still renders its 2 chips). |
| 4 | P2 | Sheet header: `!meta.caps.create` too loose for read-only; use `!create && !update && !delete`. | Fixed: §3 read-only pill now `!caps.create && !caps.update && !caps.delete`; verified `EntityCaps` (`client.ts:64`) already exposes all three, so no type change. |
| 5 | P2 | Verification step 6 commands underspecified (dotnet build target, frontend dir + `tsc -b`/`npm run build` vs root `tsc --noEmit`). | Fixed: step 1 names `QsEarlyWarning.sln` + `/run_system`; step 6 replaced with pnpm `pnpm install && pnpm build` (= `tsc -b && vite build`, per `package.json`/`tsconfig.json`) from the frontend dir, plus `dotnet test` on the Tests project. |
| 6 | P2 | Tests/docs: "if a registry-shape test asserts…" — no such test exists; specify one explicitly. | Fixed: Representative files now specifies adding `tests/QsEarlyWarning.Tests/EntityRegistryShapeTests.cs` with concrete assertions (14 grouped entities, exactly 7 groups, groupOrder matches mapping, shared group metadata, sheet-9 provenance wording — see Round 3 #1 for the corrected framing); confirmed that project already references Infrastructure. |

---

## Verification (end to end)
1. Build backend: `dotnet build /Users/hazem/hackathon/AI-QS-Challenge/QsEarlyWarning/QsEarlyWarning.sln`
   (the one solution; the API project is `src/QsEarlyWarning.Web.API/QsEarlyWarning.Web.API.csproj`).
   **One-time DB prerequisite (fresh machine).** The stack reads project data from Postgres; the run
   commands below do **not** provision it. First stand up the `qs_phase1` database once (per
   `QsEarlyWarning/db/README.md:80-84`), from the repo root:
   - `psql -d postgres -c 'CREATE DATABASE qs_phase1;'`
   - `QsEarlyWarning/db/apply.sh qs_phase1` (applies the schema migrations)
   - `dotnet run --project QsEarlyWarning/tools/QsEarlyWarning.Importer -c Release`
     (imports `data/Tower_X_Project_Data.xlsx` → slug `tower-x`)
   Then run the stack. Preferred: the **`/run_system`** project slash command (defined in-repo at
   `.claude/commands/run_system.md`). It starts the **API + dashboard** and prints the three URLs; note it
   does **not** provision the DB — it only *checks* that Postgres and `qs_phase1` already exist
   (`psql -d qs_phase1 -tAc "select count(*) from qs.projects"`) and **stops with the setup guidance above
   if that fails** (`run_system.md:12-15`). So the one-time prerequisite must already be done. It is a
   Claude Code project command, so it is invocable inside this repo even though a sandboxed reviewer without
   the command registry won't see it. **Raw fallback** (equivalent, per `QsEarlyWarning/README.md`, so the
   step is executable without the slash command; assumes the DB prerequisite above is done) — start each in
   its own background shell from the repo root:
   - API: `ASPNETCORE_ENVIRONMENT=Development dotnet run --project QsEarlyWarning/src/QsEarlyWarning.Web.API`
     (listens on `http://localhost:5070`; poll `GET /api/v1/health` for 200).
   - Dashboard: from `QsEarlyWarning/frontend/qs-early-warning`, `pnpm install` then `pnpm dev`
     (`http://localhost:5173`, proxies `/api` → :5070).
2. **Registry payload:** `GET /api/v1/entities` returns `group`/`groupLabel`/`groupOrder`/`sheetRef`/`blurb`/
   `order` on all 14 entities; every entity has a non-empty group; exactly 7 groups; the groups **sorted by
   `groupOrder`** match the mapping table order (BOQ first, System & Import last); every entity in a group
   shares the same `groupLabel`/`groupOrder` (the **nav-ordering** fields only — `sheetRef`/`blurb` are
   per-entity and may differ within a group). Confirm the lineage split in the `periods` group:
   `reporting-periods`/`cost-centre-periods` have `sheetRef == "9_HISTORICAL_DATA"` while `cost-deltas` has
   `sheetRef == null`. The default selected table (first render) is `boq-items`, NOT `estimate-versions`.
3. **Visual (browse skill):** open Data Admin → see 7 workbook sheet-tabs, sheets 1–4 labelled with their
   codes; select `2_ESTIMATE_NORMS` → chip row offers *Norms* / *Norm Materials*; select a read-only table
   (`cost-deltas`) → read-only pill shows in the header *before* interacting; breadcrumb + blurb render.
   Screenshot desktop **and** ≤760px (sheet-tabs scroll, no overflow).
4. **No CRUD regression:** create/edit/delete a `norms` row still works; FK dropdowns still resolve
   natural-key labels; `+ Add` hidden on read-only tables; row counts correct.
5. **No leakage of the mismatch:** every one of the original 14 tables is still reachable; nothing dropped;
   the **imported** sheet-9 tables reference **`9_HISTORICAL_DATA`** (their real source) and their copy is
   provenance-accurate — imported source inputs, with only the computed EVM described as derived — **not**
   mislabeled as "derived, not imported". Confirm `cost-deltas`, though it sits in the `periods` group,
   does **not** show the `9_HISTORICAL_DATA` breadcrumb: its lineage reads as live-capture (`sheetRef` null,
   ledger blurb), matching the plan's provenance note.
6. **Typecheck + build the frontend** from `QsEarlyWarning/frontend/qs-early-warning` (pnpm project — has
   `pnpm-lock.yaml`; `package.json` build script is `tsc -b && vite build`, `tsconfig.json` has
   `noEmit: true`): run `pnpm install` then `pnpm build` (i.e. `pnpm exec tsc -b && vite build`); both must
   be clean. `$B console --errors` empty on the Data Admin tab. **Run the new backend registry-shape test:**
   `dotnet test /Users/hazem/hackathon/AI-QS-Challenge/QsEarlyWarning/tests/QsEarlyWarning.Tests/QsEarlyWarning.Tests.csproj`
   (green, including `EntityRegistryShapeTests`).
