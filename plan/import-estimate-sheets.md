# Plan: Persist the estimate sheets (1–4) during workbook import

> **Execution order:** (1) plan saved here → (2) `/review_plan plan/import-estimate-sheets.md` (Codex loop
> hardens the **## Codex Review** section below) → (3) implement, matching how `enhance-data-admin-ux.md`
> was hardened. Scope was chosen by the user: **Foundation (persist-only), no schema change.**

## Context — why

The workbook import today ingests **only `9_HISTORICAL_DATA`**. `WorkbookImporter.Import()`
(`QsEarlyWarning/src/QsEarlyWarning.Infrastructure/Import/WorkbookImporter.cs`) reads the historical
panel via `IPanelLoader` (sheet 9) and inserts `reporting_periods`, a **draft** `estimate_versions`
shell, `cost_centres`, `cost_centre_baselines`, `cost_centre_plan_periods`, `cost_centre_periods` —
then publishes. It writes **zero** rows to the six estimate tables (`norms`, `norm_materials`,
`estimate_packages`, `boq_items`, `boq_norm_mappings`, `estimate_resource_lines`). Those tables exist
in the schema and are fully CRUD-wired, but stay permanently empty, so for Tower X the Data-Admin
sheet-tabs `1_BOQ` / `2_ESTIMATE_NORMS` / `3_BOQ_MAPPING` / `4_ESTIMATE_DATASHEET` show 0 rows.

The estimate graph *is* parsed elsewhere — `EstimateWorkbookLoader.Load(path)`
(`.../Infrastructure/Excel/EstimateWorkbookLoader.cs`) reads sheets 1–4 into an in-memory
`EstimateModel` — but only in memory, only for the single startup-bound `Data:EstimateProjectSlug`
project (`tower-x`), feeding idea-3 Stress Test and idea-5 resource-mix. Nothing is persisted.

**Goal (Foundation / persist-only scope, chosen by the user):** make the importer also persist the
estimate graph into the six existing tables **using their current columns** — no schema change. This
immediately: (1) fills the Data-Admin estimate tabs with real rows; (2) makes the estimate FK
dropdowns (`norm_id`, `estimate_package_id`, `boq_item_id`) resolve so those rows become
viewable/editable; (3) links `cost_centres.estimate_package_id` (null on import today); (4) runs the
existing publish-time BOQ→contract reconciliation (`fn_validate_publish` `boq_rollup_mismatch`) on real
data; and (5) lays the groundwork so a later phase can decouple idea-3/idea-5 to run per-project from
the DB. **idea-3/idea-5 keep reading the workbook for now — out of scope here.**

## Scope decisions (locked)

- **No DB migration.** Persist only into columns that exist today; richer workbook fields (sub-trade,
  margin %, procurement route, gang size, qty/unit-work, gang output) are **not** stored in this phase.
- **No feature rewiring.** `IEstimateSource` / `EstimateWorkbookLoader` / Stress Test / Variance are
  untouched; they still read the workbook. This plan only makes the importer write the DB.
- **Best-effort, backward-compatible.** A workbook missing sheets 1–4 (or with unparseable estimate
  rows) must still import successfully — estimate persistence degrades gracefully (log + skip), exactly
  as the stress test does today (`EstimateWorkbookLoader` catches and returns null).
- **Transaction safety over naive catch-and-continue (review #3).** The whole import runs in **one
  Postgres transaction** (`WorkbookImporter.cs:39 conn.BeginTransaction()`). Inside a live tx, a
  constraint/FK/CHECK violation aborts the entire transaction — every subsequent command fails with
  `current transaction is aborted`, so a C# `try/catch … continue` around a bad row does **not** recover;
  it just logs while the tx is already dead, and Validate/publish/facts downstream all fail. Therefore
  "graceful skip" must mean **the bad row never reaches the DB**: expected skips (bad `rtype`, dangling
  FK, unparseable/missing key, un-tied `total_amount`) are detected and filtered **in C#** against the
  already-parsed `EstimateModel` (all rows are in memory) *before* any INSERT. As a backstop against an
  *unexpected* DB error, wrap the estimate-persistence block in a SQL `SAVEPOINT` and `ROLLBACK TO
  SAVEPOINT` on a caught exception so the outer tx stays usable for Validate/publish/facts; a genuinely
  unexpected exception after rollback is logged and estimate persistence is abandoned (import still
  proceeds), but pre-validation is the primary mechanism — the savepoint only exists so one surprise
  can't poison the historical-data load.

---

## The work

### 1. Expose a path-based, project-agnostic estimate reader
`EstimateWorkbookLoader.Load(string path)` is a `private static` that already returns a full
`EstimateModel`. The importer needs to parse **whatever workbook it's importing**, not the
startup-bound one. Extract the parsing so both callers share it:
- Add `IEstimateWorkbookReader { EstimateModel Read(string path); }`
  (`.../Infrastructure/Excel/IEstimateWorkbookReader.cs`) and a `EstimateWorkbookReader : IEstimateWorkbookReader`
  that owns the existing sheet readers (`ReadNorms/ReadMappings/ReadResourceLines/ReadBoqLines` moved
  verbatim from `EstimateWorkbookLoader`). Keep `EstimateWorkbookLoader` as the project-gated,
  memoizing `IEstimateSource` but have it delegate parsing to the new reader (no behaviour change —
  same DTOs, same `EstimateModel`). This keeps idea-3 identical while giving the importer a clean,
  testable, path-based entry.
- **Minimal-path refactor (scope guard, review #8).** This is a *reuse* extraction, not a rewrite:
  the existing parsing logic (`ReadNorms/ReadMappings/ReadResourceLines/ReadBoqLines` and the
  `EstimateModel` assembly) moves **verbatim** into `EstimateWorkbookReader`. `EstimateWorkbookLoader`
  becomes a thin wrapper that calls `_reader.Read(path)` behind its existing project-gate + memoization,
  so its public behaviour and **existing tests stay unchanged**. Do not re-shape DTOs, sheet-column
  handling, or null/sentinel parsing. If a purely-static extraction is simpler than introducing the DI
  seam for idea-3's call site, the loader may keep calling a `static EstimateWorkbookReader.Read` while
  the importer takes the injected `IEstimateWorkbookReader` — either way the parsing code is shared, not
  duplicated, and nothing about idea-3's output changes.

### 2. Inject the reader into `WorkbookImporter` and persist between draft-create and validate
`WorkbookImporter` ctor currently takes `IPanelLoader _loader`. Add `IEstimateWorkbookReader _estimate`
(second ctor param). In `Import()`, insert a new persistence block **between the draft-version insert
(`WorkbookImporter.cs:69-72`, `versionId`, status `'draft'`) and the `Validate(...)` call (`:118`)** —
this ordering matters: the estimate-immutability trigger (`0008_estimate_immutability.sql`, on all six
tables) only allows writes while the version is `'draft'`, and `fn_validate_publish`'s
`boq_rollup_mismatch` check needs the resource lines present before publish. Reuse the existing helpers
(`InsertReturning`, `Exec`, `Num/Txt/Big/Intg/NumV`, prepared-command loops) — same pattern as the
cost-centre/baseline id-capture at `:74-95`. Wrap the block in a **SQL SAVEPOINT** (not a bare C#
try/catch — see Scope decisions / review #3): expected bad rows are filtered in C# before insert, and a
caught exception does `ROLLBACK TO SAVEPOINT` so the outer tx survives for Validate/publish/facts.

**Concrete ordered draft-time sequence (review #6 — remove placement ambiguity).** The estimate block
sits *after* the cost-centre/baseline/plan region and *before* Validate. Full order inside the tx:

1. `Purge` (`:41`) — **note the purge-order fix in §4 is a prerequisite** for step 8's CC→package link.
2. Insert `projects`, `project_memberships`, `import_runs` (`:43-55`).
3. Insert `reporting_periods` (`:57-66`).
4. Insert draft `estimate_versions` → `versionId` (`:69-72`).
5. Insert `cost_centres` (→ `ccId`) + `cost_centre_baselines` (`:78-95`).
6. Insert `cost_centre_plan_periods` (`:98-115`).
7. **NEW — estimate graph** (this plan): pre-validate in C#, then within a savepoint insert
   `norms → norm_materials → estimate_packages → boq_items → boq_norm_mappings →
   estimate_resource_lines` (order + columns below). `total_amount` is decided **before** the
   `boq_items` insert (§3).
8. **NEW — CC→package link** (§3): set-based `UPDATE qs.cost_centres … SET estimate_package_id` — runs
   here because it needs **both** `cost_centres` (step 5) **and** `estimate_packages` (step 7) to exist.
9. `Validate(...)` (`:118`) → publish/activate (`:135-137`) → facts (`:139-179`).

**Insert order + DTO→column mapping (existing columns only).** The "Columns written" cell lists
**every** column each INSERT must supply, including the tenancy columns the schema marks `NOT NULL`
(review #1). Verified against `0002_schema.sql:87-181`.

| # | Table (insert order) | Source DTO (`Domain/Estimate/EstimateModel.cs`) | Columns written (NOT-NULL cols in **bold**) | Capture |
|---|---|---|---|---|
| 1 | `norms` | `EstimateNorm` | **`project_id`**, **`estimate_version_id`**, **`norm_code`**, `description`(null), `unit`, `output_norm` | `normIdByCode[NormCode]` |
| 2 | `norm_materials` | `EstimateNorm.Mat1QtyPerUoW/Mat2QtyPerUoW` | **`project_id`**, **`norm_id`**, **`material_code`**, `qty_per_unit` — **no `estimate_version_id` column on this table** (see note) | — (see note) |
| 3 | `estimate_packages` | distinct `EstimatePackage`/`Package` codes (from mappings + resource lines) | **`project_id`**, **`estimate_version_id`**, **`code`**, `name`(null) | `pkgIdByCode[code]` |
| 4 | `boq_items` | `BoqLine` | **`project_id`**, **`estimate_version_id`**, **`boq_sec`**(Sec), **`item_ref`**(ItemRef), `description`, `unit`, `quantity`, `norm_id`(from `NormRef`→`normIdByCode`, nullable), `total_amount`(TotalAmount, **guarded/decided pre-insert**, see §3) | `boqIdByRef[(Sec, ItemRef)]` |
| 5 | `boq_norm_mappings` | `BoqMapping` | **`project_id`**, **`estimate_version_id`**, **`boq_item_id`**, **`norm_id`**, **`estimate_package_id`** (all five NOT NULL) | — |
| 6 | `estimate_resource_lines` | `ResourceLine` | **`project_id`**, **`estimate_version_id`**, **`boq_item_id`**, **`rtype`**(normalized), `norm_id`(nullable), `quantity`(TotalResourceQty), `unit_rate_amount`(UnitRate) | — |

Required-column recap (so no INSERT omits a `NOT NULL`): **all six tables carry `project_id`; five of
them also carry `estimate_version_id` — the lone exception is `norm_materials`, which has `project_id` +
`norm_id` and *no* `estimate_version_id` column at all** (`0002:101-110`). `resource_cost_amount` is a
GENERATED column and must never be in the insert list (see below).

Key rules baked into the mapping:
- **Two-pass id resolution with the correct keys.** Parents (norms, packages, boq_items) insert first
  via `RETURNING id` into dictionaries; children resolve to those ids. Use `normIdByCode[NormCode]` and
  `pkgIdByCode[code]` as before, but **`boqIdByRef` is keyed by the composite `(Sec, ItemRef)`**
  (review #5) — the DB natural key is `UNIQUE (estimate_version_id, boq_sec, item_ref)`
  (`0002:136`), so `ItemRef` alone can collide across sections. Use a `Dictionary<(string Sec, string
  ItemRef), long>` (or a normalized `"{Sec}|{ItemRef}"` string). **Every** consumer resolves by the same
  composite: `boq_norm_mappings` (`BoqMapping` carries `Sec`+`ItemRef`) and `estimate_resource_lines`
  (`ResourceLine` carries `Sec`+`ItemRef`) both look up `boqIdByRef[(Sec, ItemRef)]`, and `norm_id` still
  resolves via `normIdByCode[NormCode]`. (Note: `EstimateModel.BoqByItemRef` is keyed by `ItemRef` alone,
  which *hints* item_ref is unique in Tower X, but the composite is correct regardless and matches the
  DB constraint — belt-and-suspenders.) Mirrors the `ccId`/`baselineId` pattern at `:74-95`.
- **`resource_cost_amount` is GENERATED** (`round(quantity*unit_rate_amount,2)`) — never insert it;
  write `TotalResourceQty → quantity`, `UnitRate → unit_rate_amount`. This is the pre-computed final
  qty (the "Output Norm divisor" is already applied in the workbook column), so **no arithmetic** —
  store the raw `Total Resource Qty` directly (confirmed: the loader does no divisor math;
  `EstimateStressTester.Reconcile` only *checks* `BoqQty×QtyPerUnitWork÷OutputNorm==TotalResourceQty`).
- **`rtype` normalization** to the CHECK set `MANPOWER|MATERIAL|EQUIPMENT|SUBCONTRACT`; a resource line
  whose type can't be normalized is filtered out **in C# before the insert loop** (per review #3 — a
  CHECK violation would abort the whole tx, so it must never reach the DB), and logged to the import run.
- **Referential skips (pre-validated, not caught).** These are all decided in C# against the in-memory
  `EstimateModel` *before* inserting, so no dangling FK ever hits the DB: a `boq_norm_mappings` row is
  emitted only if its `boq_item_id` (via `boqIdByRef[(Sec,ItemRef)]`), `norm_id`, and
  `estimate_package_id` all resolved; a `resource_lines` row is emitted only if its `boq_item_id`
  resolved (its `norm_id` is nullable, so an unresolved norm → null, not a skip). Every skip is counted
  and logged to the import run. Never let a dangling reference abort the whole import.
- **`norm_materials` note (known fidelity gap — committed decision).** Sheet 2 carries only
  `Mat1/Mat2 Qty/UoW` (no material *code*); the table requires `material_code NOT NULL`. **Decision
  (locked, round-2 review #2): populate best-effort** — emit a `norm_materials` row with synthetic code
  `MAT1`/`MAT2` (and `qty_per_unit` = the corresponding `MatN QtyPerUoW`) for every norm that has a
  non-null qty for that slot, documented as lossy. This is the committed behaviour, **not** an
  either/or — so `norm_materials` is a populated, non-zero table and Verification 1b/step 3 (which
  require non-zero counts in all six tables) hold. (Real material lines are still captured faithfully and
  independently as `estimate_resource_lines` rows with `rtype='MATERIAL'`; `norm_materials` is the
  norm-recipe view.) The earlier "or leave `norm_materials` empty" fallback is **removed** to keep §2 and
  the Verification acceptance criteria consistent.

### 3. Link `cost_centres.estimate_package_id` + reconciliation safety
- **CC→package link.** `cost_centres.estimate_package_id` is never set today (`:84-87` omits it). After
  packages exist, do a set-based `UPDATE qs.cost_centres cc SET estimate_package_id = ep.id FROM
  qs.estimate_packages ep WHERE ep.project_id=cc.project_id AND ep.estimate_version_id=@v AND
  ep.code = cc.package_code AND cc.project_id=@p` (join on the `package_code` the CC already carries
  from sheet 9). Unmatched centres stay null (fine).
- **`total_amount` guard — decided BEFORE the `boq_items` insert (review #4).** Setting
  `boq_items.total_amount` arms `fn_validate_publish`'s `boq_rollup_mismatch` (`0005:240-251`): for items
  **with** resource lines and a non-null total, `Σ resource_cost_amount` must equal `total_amount` within
  `greatest(1.00, 0.005 * total_amount)` or **publish fails and the whole import rolls back**. Tower X
  reconciles (idea-3 confirms), so it passes. The decision to keep or null `total_amount` **cannot** be
  made after the row is inserted (once `fn_validate_publish` runs at `:118` it's too late, and re-updating
  a draft row just to fix it is avoidable churn) — so **pre-compute the per-item resource-line sum in C#
  first**. Concretely, before the `boq_items` insert loop:
  1. Group the parsed `ResourceLine`s by `(Sec, ItemRef)` and compute `lineSum = Σ round(quantity ×
     unit_rate, 2)` per item — this mirrors the GENERATED `resource_cost_amount` and the SQL rollup, all
     from in-memory data (no DB round-trip).
  2. For each `BoqLine`, set the insert's `total_amount` to `TotalAmount` **iff** the item has resource
     lines **and** `abs(lineSum − TotalAmount) <= greatest(1.00, 0.005 × TotalAmount)`; otherwise insert
     `total_amount = NULL` and record a reconciliation warning on the import run. (Items with no resource
     lines don't arm the check — INNER JOIN in `fn_validate_publish` — but nulling a total that has no
     lines to tie against is still the safe default.)

  So `boq_items` rows are inserted with `total_amount` **already correct**; no post-insert mutation. This
  keeps the "one bad row shouldn't fail the load" ethos while still enforcing the tie-out for rows that do
  carry a total. (The §2 sequence step 7 already places this sum-first computation before the `boq_items`
  insert.)

### 4. Re-import — Purge order MUST change (real correctness bug, review #2)
The current `Purge` (`:321-324`) deletes in this order: `… estimate_resource_lines, boq_norm_mappings,
boq_items, estimate_packages, norm_materials, norms, cost_centres`. It works **today only because
`cost_centres.estimate_package_id` is always NULL** — nothing references `estimate_packages`. The moment
this plan populates that column (§3 step 8), the FK `fk_cc_pkg (project_id, estimate_package_id)
REFERENCES estimate_packages … ON DELETE RESTRICT` (`0002:202-203`) makes deleting `estimate_packages`
while a `cost_centres` row still points at it **fail with a RESTRICT violation → the second import (and
every re-import) aborts.** So "no edit needed" is false.

**Required change to `Purge`** — delete `cost_centres` **before** `estimate_packages`. Because
`cost_centres` also depends on things deleted earlier, the safe deletion order for the estimate/CC group
becomes:
`cost_centre_periods → period_cost_deltas → cost_centre_plan_periods → cost_centre_baselines →`
`estimate_resource_lines → boq_norm_mappings → boq_items → cost_centres → estimate_packages →`
`norm_materials → norms`.
That is: pull `cost_centres` out of its current last position and move it to sit **immediately before
`estimate_packages`** (after `boq_items`, whose `fk_boq_norm`/version FKs don't touch cost centres). All
these deletes are `WHERE project_id = @p`, so only the ordering matters. *(Alternative, if minimizing the
diff to the delete list is preferred: keep the list as-is but add one statement before it —
`UPDATE qs.cost_centres SET estimate_package_id = NULL WHERE project_id = @p` — to break the RESTRICT
before `estimate_packages` is deleted. Reordering is cleaner and preferred; state whichever is chosen in
the implementation.)* Add a re-import assertion to the DB-backed test (§Verification / review #7) so this
is proven, not assumed.

**REQUIRED second edit — `ProjectAdminService.DeleteAsync` has the *identical* bug (round-2 review #1).**
The importer's `Purge` is **not** the only delete path over these tables. The API's project-delete
endpoint runs `ProjectAdminService.DeleteAsync`
(`QsEarlyWarning/src/QsEarlyWarning.Infrastructure/Postgres/ProjectAdminService.cs:112-116`), whose delete
list has the **same bad order** — it deletes `estimate_packages` before `cost_centres` (`cost_centres` is
last in the array). It works today for the same reason `Purge` does: `cost_centres.estimate_package_id`
is always NULL. The moment §3 populates that column, this endpoint will fail on
`fk_cc_pkg … ON DELETE RESTRICT` exactly like the importer. This is a second site of the identical bug,
**not optional** — apply the same fix here: move `cost_centres` to sit immediately before
`estimate_packages` in the `DeleteAsync` array (or, matching whichever variant §4 chooses for `Purge`,
null out `cost_centres.estimate_package_id` before deleting packages). Keep the comment at `:111` in sync.
(Confirmed there are exactly **two** delete paths over `qs.cost_centres` / `qs.estimate_packages` —
`WorkbookImporter.Purge` and `ProjectAdminService.DeleteAsync`; a repo-wide grep for
`DELETE FROM qs.cost_centres` / `DELETE FROM qs.estimate_packages` finds no others.)

### 4b. Entry-point ctor wiring — one line each
The CLI entry (`tools/QsEarlyWarning.Importer/Program.cs`) calls the single `Import(...)` and needs only
the extra ctor arg wired: `new WorkbookImporter(new ExcelPanelLoader(), new EstimateWorkbookReader())`.
The API composition root (`QsEarlyWarning.Web.API/Program.cs`, where `WorkbookImporter` is constructed
for the create-from-workbook / re-import endpoints) gets the same one-line ctor update.

### Representative files
- **New:** `Infrastructure/Excel/IEstimateWorkbookReader.cs`, `Infrastructure/Excel/EstimateWorkbookReader.cs`
  (parsing extracted from `EstimateWorkbookLoader`).
- **Edited:** `Infrastructure/Excel/EstimateWorkbookLoader.cs` (delegate to reader),
  `Infrastructure/Import/WorkbookImporter.cs` (ctor arg + estimate-persistence block inside a savepoint +
  CC→package link + **`Purge` reorder: `cost_centres` before `estimate_packages`**, §4),
  **`Infrastructure/Postgres/ProjectAdminService.cs` (`DeleteAsync` — same delete-order reorder as `Purge`,
  §4; REQUIRED — second site of the identical FK-RESTRICT bug)**,
  `tools/QsEarlyWarning.Importer/Program.cs` and `Web.API/Program.cs` (ctor wiring).
- **Unchanged (relied on):** `db/migrations/0002_schema.sql` (columns), `0005` (`fn_validate_publish`),
  `0008` (immutability), `EntityRegistry.cs` (six entities already CRUD-wired).

---

## Codex Review

_Automated Codex review loop (`/review_plan`). Rounds appended below; most recent last._

**VERDICT — READY FOR EXECUTION** (converged after 3 Codex rounds; no [P1] remaining). Round 3 verified
both purge paths, the required-column/FK fixes, the `(Sec,ItemRef)` composite key, the savepoint
transaction model, and the Testcontainers DB-test target against the actual schema/code, and returned
_"VERDICT: READY — no [P1] findings."_

### Round 3 — READY (0 [P1]) · final convergence check
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| — | — | None blocking. Confirmed Round 1 + Round 2 fixes are genuine against the schema/code (both delete paths, required columns, composite keys, savepoint safety, DB-test target). Codex noted the immutability trigger also covers `cost_centre_baselines`/`plan_periods` — not a blocker (all writes happen pre-publish, importer role is BYPASSRLS-exempt). | Converged — execution-ready. (Optional refinement: `import_runs` carries only `message`/`row_counts`, so "logged to the import run" means a compact summary, not per-row diagnostics — clarity nicety, non-blocking.) |

### Round 2 — CHANGES NEEDED (1 [P1], 1 [P2]) · Round 1 fixes confirmed genuine
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | §4 misses a **second** purge path with the same bug: `ProjectAdminService.DeleteAsync` (`ProjectAdminService.cs:111`) deletes `estimate_packages` before `cost_centres`. Once §3 sets `cost_centres.estimate_package_id`, API project-delete fails on `fk_cc_pkg ON DELETE RESTRICT`. Add `ProjectAdminService.cs` to required edits with the same reorder/null-out. | Fixed: verified `DeleteAsync` (`ProjectAdminService.cs:112-116`) deletes `estimate_packages` before `cost_centres` (last in array) and `fk_cc_pkg … ON DELETE RESTRICT` (`0002:202-203`); grep confirms exactly two delete paths over these tables. §4 now adds a "REQUIRED second edit" block making `ProjectAdminService.DeleteAsync` a mandatory same-fix site (not optional), and it's added to the Representative-files edit list. |
| 2 | P2 | Verification 1b (non-zero counts in all six tables) conflicts with §2's `norm_materials` note (leaving it empty is acceptable). Pick one acceptance criterion. | Fixed: §2's `norm_materials` note is now a **locked** decision to populate best-effort with synthetic `MAT1`/`MAT2` codes (non-zero); the "or leave empty" fallback removed, so all six tables are non-zero and Verification 1b/step 3 hold. Also confirmed `tests/QsEarlyWarning.Db.Tests` exists with `Testcontainers.PostgreSql` (`Phase0GateTests.cs` pattern) — Verification 1b rewritten to add the DB-backed test to that existing project (container + Docker soft-skip) instead of gating on a hand-provisioned local `qs_phase1`. |

### Round 1 — CHANGES NEEDED (6 [P1], 2 [P2])
| # | Sev | Finding | Resolution |
|---|-----|---------|------------|
| 1 | P1 | §2 mapping table omits required NOT NULL columns: `project_id` + `estimate_version_id` on packages/boq_items/mappings/resource_lines; `project_id` on norm_materials (`0002_schema.sql:101-180`). | Fixed: §2 mapping table now bolds every `NOT NULL` column and adds a "Required-column recap" — all six tables carry `project_id`; five also carry `estimate_version_id`; verified `norm_materials` has **no** `estimate_version_id` column (`0002:101-110`), only `project_id`+`norm_id`+`material_code`. |
| 2 | P1 | §4 "Purge needs no edit" is **false** once `cost_centres.estimate_package_id` is set: purge deletes `estimate_packages` before `cost_centres`, but `fk_cc_pkg` is `ON DELETE RESTRICT` (`0002:192-203`) → re-import fails. | Fixed: confirmed `fk_cc_pkg … ON DELETE RESTRICT` (`0002:202-203`) and current purge order deletes `estimate_packages` before `cost_centres` (`WorkbookImporter.cs:321-324`). §4 rewritten "Purge order MUST change": move `cost_centres` to immediately before `estimate_packages` (full order given), with a null-out alternative; also flagged as prerequisite in §2 sequence step 1 and added to Representative-files edit list + the re-import test. |
| 3 | P1 | "try/catch log + continue" is unsafe inside a PG transaction — a constraint error aborts the whole tx; catching in C# doesn't recover. Use SAVEPOINT or pre-validate so expected skips never throw. | Fixed: confirmed single tx (`WorkbookImporter.cs:39`). New Scope-decisions bullet "Transaction safety over naive catch-and-continue" + §2 now mandate pre-validating expected skips (bad `rtype`, dangling FK, unparseable) in C# before insert, with a SAVEPOINT/ROLLBACK-TO-SAVEPOINT backstop for unexpected errors; removed the "try/catch … continues" wording. Skip bullets reworded as pre-insert C# filters. |
| 4 | P1 | §3 `total_amount` guard sequencing wrong — the null-or-not decision must happen BEFORE the `boq_items` insert (or a draft-time UPDATE before Validate), not "as we insert resource lines" (item already inserted). | Fixed: §3 restructured to pre-compute per-item `lineSum` in C# (grouped by `(Sec,ItemRef)`, mirroring GENERATED `resource_cost_amount`) **before** the `boq_items` insert, then insert `total_amount` already-decided (set if tied within `greatest(1.00, 0.005×total)` per `0005:240-251`, else NULL+warn). No post-insert mutation; §2 sequence step 7 places sum-first before the insert. |
| 5 | P1 | §2 `boqIdByRef[ItemRef]` risks wrong linkage — DB natural key is `(estimate_version_id, boq_sec, item_ref)`; use composite `(Sec, ItemRef)` key. | Fixed: confirmed `UNIQUE (estimate_version_id, boq_sec, item_ref)` (`0002:136`) and that `BoqLine`/`BoqMapping`/`ResourceLine` all carry `Sec`+`ItemRef` (`EstimateModel.cs:18-33`). §2 changed capture to `boqIdByRef[(Sec, ItemRef)]` (`Dictionary<(string,string),long>`), and mappings + resource lines both resolve by the same composite; noted it's correct even though `EstimateModel.BoqByItemRef` is ItemRef-only. |
| 6 | P1 | §2 placement ambiguous — `cost_centres` are created at `:74-95` in the same range; the CC→package UPDATE must run after both packages AND cost centres exist, before validate. | Fixed: confirmed cost centres insert at `:78-95`, inside the draft→Validate window (`:69-118`). §2 adds a concrete 9-step ordered draft-time sequence placing the estimate graph (step 7) and the CC→package UPDATE (step 8, needs both `cost_centres` step 5 and `estimate_packages` step 7) after the CC/baseline/plan region and before Validate (step 9). |
| 7 | P2 | Verification: add a DB-backed import/re-import test, not just DB-less mapping assertions (real failure modes are SQL column coverage, FK resolution, tx behavior, purge/re-import). | Fixed: Verification adds step 1b — a Postgres-gated integration test that runs `Import(...)` then a SECOND import of the same slug, asserting publish success, non-zero estimate row counts, resolved `cost_centres.estimate_package_id`, and that re-import succeeds (directly exercises the #2 purge-order fix). |
| 8 | P2 | §1 refactor could be a smaller path-based wrapper around existing parsing vs moving all reader methods. | Fixed: §1 adds a "Minimal-path refactor (scope guard)" bullet — reuse extraction only, parsing moved verbatim, `EstimateWorkbookLoader` stays a thin wrapper with behaviour + existing tests unchanged; permits a static-share variant if simpler, never a rewrite. |

---

## Verification (end to end)
1. **Build + unit test (DB-less parsing/mapping).** `dotnet build QsEarlyWarning/QsEarlyWarning.sln`. Add
   `tests/QsEarlyWarning.Tests/EstimatePersistenceTests.cs` (xunit, matches sibling style): a DB-less
   test on `EstimateWorkbookReader.Read(TestData.WorkbookPath)` asserting non-empty norms/boq/mappings/
   resource-line counts and that `rtype` values all normalize into the CHECK set; plus mapping-level
   assertions (composite `(Sec, ItemRef)` id resolution, `resource_cost_amount` generated-column
   omission, and the `total_amount` tie/null decision). `dotnet test .../QsEarlyWarning.Tests.csproj`.
1b. **DB-backed import + RE-IMPORT integration test (review #7).** DB-less mapping tests can't catch the
   real failure modes this plan touches — `NOT NULL` column coverage (#1), FK resolution, transaction
   abort behavior (#3), and above all the **purge/re-import ordering (#2, both `Purge` *and*
   `ProjectAdminService.DeleteAsync`)**. Add this test to the **existing `tests/QsEarlyWarning.Db.Tests`
   project** (round-2 review #2) — it already references `Testcontainers.PostgreSql` and spins up a real
   `postgres:17` container that applies the migrations (see `Phase0GateTests.cs`), so mirror that pattern
   rather than gating on a hand-provisioned local `qs_phase1`: reuse the container + migration apply, and
   soft-skip when Docker is unavailable exactly as `Phase0GateTests` does (`IsDockerUnavailable`). The
   test: runs `Import(...)` against the containerized DB, asserts (a) `report.Passed` / version published,
   (b) non-zero row counts in **all six** estimate tables (including `norm_materials`, per the committed
   §2 synthetic-code decision) tied to the published version, (c) `cost_centres.estimate_package_id`
   resolves for > 0 centres, and (d) **a SECOND `Import(...)` of the same slug also succeeds** — this
   second run exercises the `Purge` reorder and would fail loudly against the current delete order. Assert
   the re-import leaves consistent counts (no duplication, no RESTRICT error). If practical, also exercise
   `ProjectAdminService.DeleteAsync` after import to prove the second delete path's reorder.
2. **Re-import Tower X** (needs Postgres/`qs_phase1`; one-time DB setup per `QsEarlyWarning/db/README.md`
   if fresh — `CREATE DATABASE qs_phase1` → `db/apply.sh qs_phase1`):
   `dotnet run --project QsEarlyWarning/tools/QsEarlyWarning.Importer -c Release`. Import must still
   **pass** (`report.Passed`, version published) — i.e. the new estimate rows don't break publish
   validation.
3. **DB row counts** (should now be non-zero, tied to the published version):
   `psql -d qs_phase1 -c "select 'norms',count(*) from qs.norms union all select 'norm_materials',count(*)
   from qs.norm_materials union all select 'boq_items',count(*)
   from qs.boq_items union all select 'estimate_packages',count(*) from qs.estimate_packages union all
   select 'boq_norm_mappings',count(*) from qs.boq_norm_mappings union all select
   'estimate_resource_lines',count(*) from qs.estimate_resource_lines;"` (all six non-zero) and confirm
   `select count(*) from qs.cost_centres where estimate_package_id is not null` > 0.
4. **Reconciliation held.** `select * from qs.fn_validate_publish(<pid>,<vid>)` returns no
   `boq_rollup_mismatch` rows (or only the items we intentionally nulled, logged on the import run).
5. **UI (browse skill / `/run_system`).** Data Admin → `1_BOQ Bill of Quantities`, `2_ESTIMATE_NORMS`,
   `4_ESTIMATE_DATASHEET` now show rows; open the `boq-mappings` add form and confirm the `norm_id` /
   `estimate_package_id` / `boq_item_id` FK dropdowns are **populated** (were empty before). No console
   errors; existing tabs (EVM, Cost Centres) unaffected.
6. **No regression to idea-3/idea-5.** Stress Test + Variance still render for Tower X exactly as before
   (they still read the workbook). Full `dotnet test` suite green.
