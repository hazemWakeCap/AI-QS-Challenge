# Plan — Digitalize the QS Cost Workbook: a Postgres-Backed System of Record

> Companion to `idea-1-early-warning-classifier-implementation-plan.md`. That plan built the
> read-only early-warning + copilot over a frozen Excel file; this plan turns the workbook itself
> into a live, multi-project system of record with a data-entry dashboard and computed EVM, and
> re-homes the existing analytics on top of it.

---

## 1. Context — why do this

Today the solution reads a single Excel workbook as immutable, read-only source. That workbook is
**already a normalized relational database in disguise** — five sheets with verified referential
integrity (0 orphan foreign keys), a cost model that reconciles end to end, and a time-series fact
table. Excel is being used as *both* the system-of-record *and* the UI, badly: no concurrency, no
validation, no audit trail, no multi-project, no API, and — the core pain from `PROBLEM.md` — a QS
"spots a problem weeks too late, after the invoices are paid."

The deeper insight the withheld sheets hand us: **the budget / PV / EV / KPI / EVM sheets were
intentionally excluded because they are *derived*, not authored.** A QS in Excel manually maintains
what a database should *compute*. Each month the only genuine human inputs per cost centre are
**actual % complete** and **actual cost** (by resource split). Everything else — PV, EV, CV, CPI,
SPI, EAC, VAC, %-budget-consumed, the AMBER alert, rolling-3M CPI — is arithmetic.

**Digitalizing** means: move the five sheets into PostgreSQL as a multi-project system of record,
give the QS a **data-entry dashboard** (full CRUD) instead of a spreadsheet, **compute EVM live** in
the database, and run the analytics/AI we already built (early-warning watchlist + Claude copilot) on
**live data** instead of a frozen file. This turns a single-project point solution into a product.

Prior work in this repo is all read-only analysis over the frozen workbook — **this digitalization,
multi-project, data-entry direction is net-new** and does not contradict any existing idea; it
subsumes them as features on top of the platform.

---

## 2. Evaluation of the vision (answering the "think hard" ask directly)

**Q: Can we replace the Excel sheets with a dashboard where records are added and features built on
top?** — **Yes, cleanly.** The current architecture already has the exact seam for it (see §3): the
whole scoring/watchlist/copilot stack reads an in-memory `IReadOnlyList<CostCentrePeriod>` behind one
interface. Swap the source from Excel to Postgres and everything downstream keeps working.

**Q: Does it apply to *all five* sheets — 1_BOQ, 2_ESTIMATE_NORMS, 3_BOQ_MAPPING,
4_ESTIMATE_DATASHEET, 9_HISTORICAL_DATA?** — **Yes**, and they split into two natural tiers:

| Tier | Sheets | Nature | In the product |
|---|---|---|---|
| **Estimate (reference)** | 1_BOQ, 2_ESTIMATE_NORMS, 3_BOQ_MAPPING, 4_ESTIMATE_DATASHEET | Set once at tender; the priced baseline + recipes + rates | Entered/edited in a **setup dashboard**; defines BAC and the resource-cost build-up |
| **Progress (facts)** | 9_HISTORICAL_DATA | Time-series, one row per cost centre per month | The recurring **monthly capture** screen; EVM computed from it + the estimate |
| **Derived (withheld)** | budget/PV/EV/KPI/EVM | Not a sheet at all — computed | Live DB views / generated columns; never hand-typed |

Verified structure that makes this safe: `Norm Code` joins norms↔mapping↔datasheet with 0 orphans;
`Package_Code == Estimate Package` links the estimate to every historical cost centre; each BOQ item
explodes into MANPOWER/MATERIAL/EQUIPMENT/SUBCONTRACT resource lines whose costs roll up to the BOQ
total. These become **DB foreign keys and reconciliation checks** — integrity guaranteed, not hoped.

**Honest cons (why this is a platform, not a weekend):**
- It's a real product build — full CRUD for 5 entities + validation + auth + multi-project isolation
  is weeks, not hours. Mitigated by strict phasing (§7): each phase is independently shippable.
- Migrating a QS off Excel is a change-management problem, not just a tech one; the importer (§5c)
  keeps Excel as an on-ramp, not a wall.
- The organiser-generated single-project dataset can't validate cross-project claims — the platform
  *enables* multi-project, but real predictive generalisation still needs real projects over time.

**Verdict: worth doing, high-leverage, and the current codebase is unusually well-positioned for it.**

---

## 3. The core architectural insight — one seam does most of the work

The reuse inventory found the entire data dependency funnels through a single interface in
`QsEarlyWarning.Infrastructure`:

```csharp
public interface IPanelLoader {
    IReadOnlyList<CostCentrePeriod> Load(string workbookPath);
}
```

`ExcelPanelLoader` (ClosedXML) is its only implementation, and it is the **only** class in the whole
solution bound to Excel. Everything else — `Domain` (the `CostCentrePeriod` record + `EvmSnapshot`),
all of `Core` (scoring, rolling-origin evaluation, `ModelProvider`), the `Agent` copilot + its tools,
all 4 Web.API controllers, and the entire React frontend — consumes `snapshot.Panel` and is **agnostic
to where the data came from.**

**Therefore:** a new `PostgresPanelLoader` that projects the computed-EVM view into `CostCentrePeriod`
records lets the early-warning watchlist **and** the copilot run on live database data. The digitalization
is *additive*, not a rewrite.

> **Codex correction (Findings 1, 2):** the seam is **not** a one-line loader swap. Two implemented
> realities block that: (a) the analytics are single-project and period-hard-coded (`EvmThresholds`
> pins `MinTrainOrigin=4`, `LastLabeledPeriod=11`, `ForecastPeriod=12`; `ModelProvider` holds one global
> `ModelSnapshot`), and (b) `IPanelLoader`/`IModelProvider` are **singletons** while an EF `DbContext` is
> scoped and not thread-safe. So the real seam is a **project-aware, async snapshot/model registry**: keyed
> by `project_id`, origins derived from the DB (forecast = latest open period, not a constant), reads via
> `IDbContextFactory` materialising a detached immutable snapshot. Still additive to `Domain`/`Core`/`Agent`
> — but a registry, not a single line. Details in §5b/§6.

---

## 4. Target architecture

```
                         React dashboard (Vite + TS)
   ┌───────────────┬─────────────────┬──────────────┬─────────────────────┐
   │ Estimate CRUD │ Monthly progress│  Watchlist   │  EVM dashboard +     │
   │ (BOQ/norms/…) │ capture (actuals)│ (early-warn) │  Copilot (chat)     │
   └───────┬───────┴────────┬────────┴──────┬───────┴──────────┬──────────┘
           │  REST /api/v1  │               │                  │
   ┌───────▼────────────────▼───────────────▼──────────────────▼──────────┐
   │  ASP.NET Core 8 Web.API  (tenant middleware + auth from Phase 3)      │
   │   CRUD controllers (projects, reporting-periods, boq, norms, mappings,│
   │   datasheet, progress) + Watchlist / ValidationSummary / Copilot / Health│
   ├──────────────────────────────────────────────────────────────────────┤
   │  Core (UNCHANGED math): scoring, rolling-origin eval, copilot tools   │
   │        — all read IReadOnlyList<CostCentrePeriod>                      │
   ├──────────────────────────────────────────────────────────────────────┤
   │  Infrastructure                                                       │
   │   • Project-aware async snapshot/model REGISTRY (replaces ModelProvider)│
   │       └ PostgresPanelLoader via IDbContextFactory → detached snapshot │
   │   • EF Core QsDbContext + CRUD repositories (auth, concurrency, audit) │
   │   • Staging Importer (import_run → validate → atomic activate; ClosedXML)│
   ├──────────────────────────────────────────────────────────────────────┤
   │  PostgreSQL  (plain; Timescale deferred to Phase 5)                   │
   │   projects · reporting_periods · cost_centres · estimate_packages ·   │
   │   norms(+materials) · boq_items · boq_norm_mappings · resource_lines ·│
   │   estimate_versions · cost_centre_baselines · cost_centre_periods ·   │
   │   cost_centre_evm (VIEW: CPI/SPI/EAC/alert/rolling)                   │
   └──────────────────────────────────────────────────────────────────────┘
```

---

## 5. Postgres schema (concrete)

> **Revised twice after codex.** Round 1 added `reporting_periods`/`cost_centres`, composite FKs, `NUMERIC`,
> plain Postgres. Round 2 found several round-1 fixes **mutually incompatible** and demanded four
> foundational choices be frozen first. Those are frozen below (§5.0), then the schema is corrected.

### 5.0 Four frozen foundational choices (codex readiness gate)

These are decided here with recommendations; **flagged for your veto** in the round-2 reconciliation log.

1. **Baseline projection → snapshot onto the fact, but split the stable baseline from the time-phased plan
   curve (Findings 1, 2r-1).** A generated column can't query another table, so `ev/pv` are generated from
   values **on the fact row**. But `plan_pct_complete` is **period-specific** (an S-curve that changes every
   period) — it can't live on a single per-cost-centre baseline row. **Decision:**
   - `cost_centre_baselines` holds only the **stable** numerics (`bac`, `budget_qty`) per version.
   - a new **`cost_centre_plan_periods`** table, keyed `(project_id, estimate_version_id, cost_centre_id,
     reporting_period_id)`, holds the **time-phased** plan curve. **One stored source of truth** (`planned_pct`
     **or** `planned_qty`) with the other **derived** — or their equality **validated at publication**.
     **Validation policy (corrected, Finding 1 of 3rd review):** enforce `0 ≤ planned_pct ≤ 100` and
     **non-decreasing across consecutive planned periods** — **but not a blanket "ends at 100% within the
     imported horizon."** The supplied workbook has monotonic curves, yet **166 of 173 centres end period 12
     below 100%** (73–99%) because Tower X is still underway; a blanket "ends at 100%" rule would reject valid
     source data. Require 100% **only** when the version's declared schedule horizon contains the centre's
     planned-finish period, or the centre is explicitly marked plan-complete — **never** merely at the latest
     currently-imported reporting period.
   - **at period open**, one transaction copies `bac`, `budget_qty` (from baseline) **plus that period's**
     `planned_pct` (from the plan curve) onto the fact; `ev/pv/earned_qty` generate row-locally.
   - **Immutability is enforced by privilege, not convention (Findings 5r-1, 4 of 3rd review):** a trigger
     alone **cannot** recognize an "authorized transaction" — an app role holding direct `UPDATE` on the
     snapshot columns could invoke the same SQL path or spoof a flag. So **direct `UPDATE` on the
     `bac`/`budget_qty`/`planned_pct` snapshot columns is revoked from the app and worker roles**; period-open
     and rebaseline run only through **explicit transactional procedures owned by a narrowly-scoped service
     role** (`SECURITY DEFINER`, fixed safe `search_path`, fully-qualified objects). A trigger rejecting any
     snapshot-column update on **closed** periods is kept as **defense-in-depth**, but privilege + procedure
     ownership is the real authorization boundary — otherwise generated PV/EV would silently drift.
2. **Cost storage → cumulative snapshot now, append-only ledger after a *defined* cutover (Findings 2, 2r-2).**
   Import + Phases 0–2 use `ac_*_cumulative` fact columns (import-compatible interim; correction/
   reclassification behaviour defined; not assumed monotonic). **At Phase 3** introduce the append-only
   `period_cost_deltas` ledger. **The cutover is a defined one-time migration** (not ad-hoc SQL): convert
   consecutive cumulative snapshots → per-resource **opening balance + deltas** (handling the first period,
   missing periods, negative corrections, reclassifications); **after cutover, cumulative fact columns become
   read-only** and `cost_centre_evm` reads **one canonical ledger-derived cumulative view** — never two
   writable sources of AC.
3. **Version ownership of the whole estimate graph (Finding 3).** Versioning only baselines is insufficient
   while `norms/boq_items/mappings/packages/resource_lines` stay project-global mutable — editing them
   rewrites an older published estimate and re-import collides on unique keys. **Decision:** every authored
   estimate entity carries `estimate_version_id`; uniqueness is **version-scoped** (`(estimate_version_id,
   natural_key)`); a per-project **active-version pointer** selects the live graph; **publication atomically
   validates + activates a complete graph and published versions are immutable**. Staging/rollback then work.
4. **Tenant boundary → RLS backed by real membership, with worker semantics + PG-correct view/identity design
   (Findings 5, 6, 10, 3r-1, and 2–3 of the 3rd review).** Composite FKs stop corrupt references but *not
   disclosure*; and `SET LOCAL app.current_project` alone only proves a caller *supplied* an id, not that they
   may see it. **Decision:**
   - a **`project_memberships(user_id, project_id, role)`** table. **Transaction-local identity is two
     settings — `app.current_user_id` *and* `app.current_project_id`** — both validated against trusted
     authentication **before** the transaction opens; a caller-set project GUC alone is never trusted.
   - **Non-recursive membership design (Finding 3 of 3rd review):** the tenant-table RLS predicate resolves
     membership through a **narrowly-scoped, owner-safe `SECURITY DEFINER` function** (fixed safe `search_path`,
     fully-qualified objects); `project_memberships` itself carries a **non-recursive** policy keyed directly on
     `app.current_user_id`, so applying the predicate to other tables cannot recurse back through it.
   - `ENABLE`+`FORCE ROW LEVEL SECURITY` for the app role; transaction-local context; pooled-connection reset.
   - the async registry/importer has **no HTTP request** → a **least-privilege worker role** that sets its
     target project inside its own transaction; a tightly controlled **bypass role for migrations/purge only**.
   - **RLS is a table mechanism, not a view mechanism (Finding 2 of 3rd review).** A plain view runs with its
     owner's privileges and would **bypass** underlying RLS. So: **pin PostgreSQL 15+** and define
     `cost_centre_evm` as a **`WITH (security_invoker = true)`** view (or an invoker-safe function); the **view
     owner and app role must not hold `BYPASSRLS`**, and **the app role must not own the tenant tables**. The
     EVM view itself is integration-tested for cross-project leakage as **both the app and worker roles** — not
     assumed safe by ownership.
   - **Authentication + membership authorization land in Phase 2**, before the first multi-project read. Tests:
     spoofed context, missing context, pooled-connection reuse, worker access, membership revocation, **a user
     belonging to multiple projects, and a removed membership**.

### 5.1 Entities

Multi-project root → **versioned estimate graph** → dimensions → facts. Every table carries `project_id`;
**every parent relationship is a composite FK `(project_id, parent_id) → parent(project_id, id)`**. Money is
**`NUMERIC`**, columns named **`*_amount`** (neutral) with the project's **immutable reporting currency**
(Finding 8 — no `_aed` in names, no cross-currency summing). Delete behaviour: **`RESTRICT` on published
estimate/baseline + closed-period history**; projects use **archival/soft-delete + an authorized purge**, not
blanket `ON DELETE CASCADE` (Finding 11).

| Table | Source | Key columns / notes |
|---|---|---|
| `projects` | — | `id`, `slug`, `name`, `reporting_currency` (immutable once monetary data exists); tenant root |
| `reporting_periods` | derived | `id` + `UNIQUE(project_id,id)`, `UNIQUE(project_id,period_id)`, `UNIQUE(project_id,period_start)`; `status` (open/closed), `opened_at`, `closed_at` — source of the `period_id ↔ period_start` 1:1 map + open/close workflow (Finding 4) |
| `estimate_versions` | derived | `id`, `status` (draft/published/superseded), effective dates, `source_hash`; a per-project **active-version pointer**; published = immutable |
| `norms` (+ `norm_materials` 0..2) | 2_ESTIMATE_NORMS | owned by `estimate_version_id`; `UNIQUE(estimate_version_id, norm_code)` |
| `estimate_packages` | derived (`EP-…`) | owned by `estimate_version_id`; `UNIQUE(estimate_version_id, code)` |
| `boq_items` | 1_BOQ | owned by `estimate_version_id`; `UNIQUE(estimate_version_id, sec, item_ref)`; composite FK → `norms` |
| `boq_norm_mappings` | 3_BOQ_MAPPING | version-scoped; composite FKs → `boq_items`, `norms`, `estimate_packages` |
| `estimate_resource_lines` | 4_ESTIMATE_DATASHEET | version-scoped; composite FK → `boq_items` + `norms`; `rtype`; `unit_rate_amount`; `resource_cost_amount` generated = qty × rate; rolls up to `boq_items.total_amount` (finalization check, §9) |
| `cost_centre_baselines` | derived | per-version, per-cost-centre **stable** numerics only: `bac`, `budget_qty` (Finding 1r) |
| `cost_centre_plan_periods` **(new, Finding 1r; policy corrected, 3rd review)** | derived | time-phased plan curve; `(project_id, estimate_version_id, cost_centre_id, reporting_period_id)`; **one stored source of truth** (`planned_pct` **or** `planned_qty`), other derived/equality-validated; `0 ≤ planned_pct ≤ 100`, non-decreasing across consecutive planned periods; **100% required only when the schedule horizon reaches planned-finish or the centre is marked plan-complete — not at the latest imported period** (166/173 Tower X centres are <100% at P12) |
| `cost_centres` | 9_HISTORICAL_DATA | project master of `bcc_id` identity (WBS/package/discipline/unit) **+ `effective_start_period` / `effective_end_period`** bounding its active range (Finding 4r — so the calendar spine knows which centre-periods to expect); the fact FKs this |
| `cost_centre_periods` | 9_HISTORICAL_DATA | fact; composite FKs `(project_id, cost_centre_id)` + `(project_id, reporting_period_id)`; **`UNIQUE(project_id, cost_centre_id, reporting_period_id)`**; snapshots `bac, budget_qty, planned_pct` at open; **one row required per active centre-period** (absence = data-contract violation, Finding 4r) |
| `project_memberships` **(new, Finding 3r)** | — | `(user_id, project_id, role)`; resolves the tenant RLS predicate via an owner-safe `SECURITY DEFINER` function; **carries a non-recursive policy keyed on `app.current_user_id`** so it can't recurse (3rd review) |
| `period_cost_deltas` **(Phase 3, Finding 2r)** | live capture | append-only ledger (resource type, amount, effective period, posting/reversal, idempotency key); **the single canonical AC source post-cutover** |

**The fact table — inputs vs derived (contradiction resolved):**
- *References (provenance):* `cost_centre_id`, `reporting_period_id`, `baseline_id`, `estimate_version_id`.
- *Snapshotted at period open (immutable; privilege- + trigger-enforced — Choice 1):* `bac`, `budget_qty`
  (from the baseline) + **`planned_pct`** (that period's value from `cost_centre_plan_periods`). Direct
  `UPDATE` on these columns is **revoked from app/worker roles**; they change only through the period-open /
  rebaseline `SECURITY DEFINER` procedures. On the fact row so generated columns are valid and closed history
  is frozen regardless of later estimate edits.
- *Actual inputs (cumulative-to-date interim; ledger-derived post-cutover — Choice 2):* `actual_pct_complete`,
  `ac_material_cumulative`, `ac_manpower_cumulative`, `ac_equipment_cumulative`, `ac_subcontract_cumulative`,
  `lifecycle`.
- *Derived generated STORED (row-local, valid because inputs are on-row):* `ac_total = Σ ac_*_cumulative`,
  `pv = planned_pct/100·bac`, `ev = actual_pct_complete/100·bac`, `earned_qty = actual_pct_complete/100·budget_qty`.

**Computed EVM — `cost_centre_evm` view** is the single source of truth (reads the on-fact snapshot
numerics, so no cross-table dependency). It is a **`WITH (security_invoker = true)`** view (PostgreSQL 15+ —
Finding 2 of 3rd review) so it enforces the querying role's RLS instead of the owner's; the view owner and
app role hold no `BYPASSRLS`, and the app role does not own the tenant tables:
```sql
cpi = ev / NULLIF(ac_total,0)     spi = ev / NULLIF(pv,0)     cv = ev - ac_total
eac = bac*ac_total / NULLIF(ev,0) vac = bac - eac             pct_budget_consumed = 100*ac_total/NULLIF(bac,0)
alert_level = CASE lifecycle WHEN NOT_STARTED/CLOSED THEN that ELSE (cpi<0.95 ? AMBER:GREEN) END
```
Generated columns are STORED-only and can't chain, so `cpi` lives in the view.

**Rolling-3M CPI — calendar spine from an explicit "active" contract (Findings 5, 4r).** A bare
`RANGE INTERVAL '2 months' PRECEDING` only averages *rows that exist*, and a *missing* row is ambiguous
(missing data vs not-started vs closed) if lifecycle only lives on the fact. **Decision:** the "expected"
centre-periods come from **`cost_centres.effective_start_period…effective_end_period`** (the active-range
contract), and **one fact row is required per active centre-period** (absence = data-contract violation).
The spine is that cross-product; require **three consecutive present observations else `null`** — one
declared policy that **matches `FeatureBuilder`'s exact-predecessor lag rule** in C#. Cross-test with
fixtures for a **missing middle month** and **lifecycle transitions** so SQL rolling CPI and the C#
exact-lag features agree on the same rows.

**TimescaleDB — deferred (codex).** At ~2K rows/project a hypertable isn't justified and would make the
guaranteed Phase 0 depend on an extension + raw DDL + version pinning. **Start with a plain PostgreSQL
table** + indexes on `(project_id, bcc_id, period_start)` and `(project_id, period_start)`; design the key
so conversion stays possible if measured volume warrants it. *(Factual correction to the earlier draft:
current Timescale docs allow regular↔hypertable FKs in both directions — only hypertable→hypertable is
unsupported; the partition-column-in-every-unique-index rule stands.)* This revises the earlier "EF Core +
TimescaleDB" decision — flagged for you in the reconciliation log; easy to re-enable if you'd rather keep it.

### 5b. EF Core + the project-aware snapshot provider (not a one-line swap)

New EF Core layer in `Infrastructure` (`QsDbContext` + `Npgsql.EntityFrameworkCore.PostgreSQL`):
`HasPostgresEnum<>` for stable enums (use `TEXT` + CHECK for **evolving workflow states** like estimate
status — Finding 14); generated columns via `.HasComputedColumnSql(sql, stored:true)` +
`AfterSaveBehavior.Ignore`; the EVM view as `.HasNoKey().ToView("cost_centre_evm")`.

**Service-lifetime fix (Finding 2):** a `DbContext` is scoped and not thread-safe, so it **cannot** back a
singleton loader. Use `IDbContextFactory<QsDbContext>` inside a **singleton project-aware model registry**;
reads are **async + cancellation-aware** and materialise a **detached immutable snapshot** before the
context is disposed:
```csharp
public async Task<IReadOnlyList<CostCentrePeriod>> LoadAsync(long projectId, CancellationToken ct) {
    await using var db = await _factory.CreateDbContextAsync(ct);
    var rows = await db.Evm.AsNoTracking()
        .Where(r => r.ProjectId == projectId && EF.Functions.Like(r.PackageCode, "EP-%"))
        .ToListAsync(ct);
    return rows.Select(Project)                                  // "NOT_STARTED" → "NOT STARTED"
               .OrderBy(x => x.BccId, StringComparer.Ordinal)    // Ordinal in memory (matches Excel exactly)
               .ThenBy(x => x.PeriodId).ToList();
}
```
The registry keys snapshots by immutable `project_id`, derives reporting origins from `reporting_periods`
(forecast = latest open period, **not** a hard-coded 12 — Finding 1), and defines cache invalidation,
concurrent-rebuild de-duplication, last-known-good, and bounded eviction.

**Tenant boundary = RLS (Choice 4).** Each request opens a transaction that `SET LOCAL` **both**
`app.current_user_id` **and** `app.current_project_id` (never a project id alone — Finding 3 of 3rd review),
each validated against trusted authentication **before** the transaction opens, against an app role with
`FORCE ROW LEVEL SECURITY`, no `BYPASSRLS`, and **no ownership of the tenant tables**; the membership
predicate resolves through the owner-safe `SECURITY DEFINER` function (Choice 4). Pooled connections reset
**both** settings between uses; a test suite exercises cross-tenant reads *and* writes. This is a real
boundary, not an EF query filter.

**The worker/importer is a service principal, not an anonymous bypass.** Having no HTTP request, the async
registry and importer run as a **least-privilege worker role** that is **also RLS-governed** — it holds **no
`BYPASSRLS` and owns no tenant tables**, is granted membership to the specific project(s) it services via
`project_memberships` (a service-principal `user_id`), and sets **both** `app.current_user_id` (its service
principal) and `app.current_project_id` inside its own transaction so the same membership predicate applies.
Only the tightly-scoped **bypass role for migrations/purge** may sidestep RLS. The `security_invoker` EVM
view is leakage-tested under the worker role as well as the app role.

**Snapshot consistency = a project `data_revision` (Finding 7, specified).** Every write that affects the
panel increments `projects.data_revision` **in the same transaction**. A rebuild: reads the panel + active
estimate version under **one transaction snapshot**, records that `data_revision`, trains *after*
materialization (off the request path), then **activates only if the current `data_revision` still matches**
— otherwise it retries/coalesces. Persist `{ project_id, source_revision, source_fingerprint, reporting_cutoff,
scorer_version, build_status, failure_reason, timestamps }`; reads return the active snapshot's
`source_revision` so **staleness is observable**. This replaces `ModelProvider`'s single global snapshot.

### 5c. Excel → Postgres importer (staging + atomic activation)

`IWorkbookImporter.Import(workbookPath, projectSlug)` reuses ClosedXML + `ExcelPanelLoader`'s
sentinel/`Num()` parsing, inserting in FK order (projects → reporting_periods → norms(+materials) →
estimate_packages → boq_items → mappings → resource_lines → estimate_versions/baselines → cost_centres →
cost_centre_periods), **deriving nothing computed**.

**Safe replacement (Finding 8):** `TRUNCATE` can't scope to one project. Import into **staging tables under
an `import_run`**, validate row counts / composite FKs / reconciliation / computed EVM, then **atomically
activate** the new estimate/baseline version; keep the previous active version for rollback; record
source-file hash, importer version, actor, timestamps. Not delete-first.

**Reconciliation report (Finding 11, corrected):** compare CV/CPI/SPI/EAC/VAC **and alert outcomes** against
the workbook's withheld columns with **field-specific tolerances** (handling zero denominators + sentinel/
lifecycle cases) — not one blanket `~0.5%`, and not a "byte-for-byte" promise (Excel/`double`/`NUMERIC`
rounding differs). Passing this still proves the thesis: those KPIs were derivable all along.

---

## 6. Compatibility map — what stays, what changes (honest — Finding 9)

Not "reused unchanged." Only the leaf math is untouched; project-aware dynamic periods ripple through most
layers.

| Component | Status | Why |
|---|---|---|
| `Domain` records + `EvmSnapshot` projection | **kept** | pure row-local math |
| `Core` scoring / rolling-origin / feature math | **kept** | operates on an `IReadOnlyList<CostCentrePeriod>` |
| `ModelProvider` | **replaced** | → project-scoped async registry |
| `EvmThresholds` period constants (4/11/12) | **changed** | → per-project values from `reporting_periods` |
| `RollingOriginEvaluator`, controllers, copilot tools | **changed** | take the project's origins as input, not constants |
| DTOs / routes | **changed** | project scoping + concurrency tokens |
| React state | **changed** | project selector; live data |

**New:** EF Core `QsDbContext` + migrations; the async registry + `PostgresPanelLoader` (`IDbContextFactory`);
the staging importer; CRUD repositories with auth; tenant/RLS middleware; the ledger (Phase 3).

**Regression tests required (Finding 9):** period **13+**, projects with **fewer than 4 origins**,
**independent per-project cutoffs**, **cache eviction**, and **simultaneous rebuilds** (data-revision race).

---

## 7. Phasing — revised sequence (round 2: tenancy in Phase 2, DDL-first Phase 0)

- **Phase 0 — Frozen DDL + staging importer, *proven in Postgres*.** The §5.0 choices + the five round-3
  contracts + the **five third-review corrections** resolved; then **reviewed, executable DDL** (PostgreSQL
  **15+ pinned**, for `security_invoker` views). The Phase-0 DDL package **must include**:
  `cost_centre_plan_periods` (time-phased plan curve, **one-source-of-truth + workbook-safe monotonic policy,
  not "ends-at-100%"**), `project_memberships` + a **non-recursive** membership policy via an owner-safe
  `SECURITY DEFINER` lookup, table RLS on every tenant table **plus a `security_invoker` `cost_centre_evm`
  view** (app/owner roles without `BYPASSRLS`; app not owning tenant tables), **revoked direct `UPDATE` on
  snapshot columns + period-open/rebaseline transactional procedures** (`SECURITY DEFINER`, fixed
  `search_path`), the **lifecycle-expectation model** (`cost_centres.effective_start/end` +
  one-row-per-active-centre-period contract), the **ledger cutover/backfill** tables/SQL, snapshot-immutability
  triggers (defense-in-depth), composite-FK indexes with **leading-column order matching project-scoped
  joins**, `RESTRICT` deletes, and `NUMERIC` checks. **Exit gate:** Testcontainers migration **+ transaction**
  tests proving composite tenant FKs, version immutability, unique period/fact keys, **period-open
  snapshotting**, **concurrent estimate-publish vs period-open**, **ledger-cutover reconciliation**,
  **closed-period mutation rejection**, **rebaseline audit/`data_revision` updates**, publish finalization,
  staging rollback; plus the third-review gates: the **workbook-backed plan-curve regression test** (166/173
  centres <100% at P12 must import cleanly), **`security_invoker` EVM-view cross-tenant tests** (app *and*
  worker roles), **non-recursive membership-policy tests** (multi-project user, removed membership, spoofed/no
  setting), **privilege/procedure tests** (app role cannot directly `UPDATE` snapshot columns), and
  **publication + period-close completeness tests** (missing active-centre facts fail close with a typed list;
  decreasing/duplicate/missing plan points fail publish); and RLS isolation (spoofed/missing context, pooled
  reuse, worker role, membership revocation) — **not** a promised appendix.
- **Phase 1 — Import/reconciliation report** matching the DB view to the workbook within **field-specific
  tolerances** (CV/CPI/SPI/EAC/VAC + alert). The thesis proof.
- **Phase 2 — Auth + tenancy + read-path swap.** **Authentication and project-membership authorization land
  here** (Findings 6, 10) — *before* any project-selectable read. RLS boundary live; async project-aware
  registry serves watchlist + copilot from Postgres; Excel retained as a comparison adapter for identical
  rankings.
- **Phase 3 — Authenticated capture + the cost ledger.** Write permissions, optimistic concurrency
  (`data_revision`/version token), idempotency keys, append-only audit, close/reopen; introduce the
  `period_cost_deltas` ledger (Choice 2); **asynchronous, coalesced, data-revision-guarded** snapshot refresh
  (a save enqueues a rebuild; never blocks on retraining).
- **Phase 4 — Estimate authoring + atomic version publication + explicit rebaseline** (draft/published/
  superseded; published graphs immutable; closed periods keep their snapshot).
- **Phase 5 — Scale/portfolio.** Timescale only if measured scale justifies it; cross-project copilot/
  portfolio (with an explicit FX policy — Finding 8) only after tenant isolation is proven.

**Cut-line:** Phases 0–2 are the guaranteed slice (live early-warning on a real, correct, isolated DB). 3–4
are the product core. 5 is scale.

---

## 8. Verification

- **Phase 0/1:** importer run → per-table counts equal workbook (boq ~229, norms ~189, mappings ~173,
  datasheet ~773, cost_centre_periods 2076); composite-FK integrity returns 0 cross-tenant/orphan rows; the
  finalization procedure confirms resource lines roll up to BOQ totals within tolerance; the reconciliation
  report matches CV/CPI/SPI/EAC/VAC + alert per field-specific tolerances.
- **Phase 2:** with the registry wired, `GET /api/v1/watchlist?period=8&k=5` returns the **same** top centres
  as the Excel adapter (e.g. BCC-ARC-PAINT-317); all 32 existing tests green against a test Postgres
  (Testcontainers); `cost_centre_evm` CPI/EV/PV match recorded values (`EvmIdentityTests` contract); AMBER ≡
  CPI < 0.95 holds.
- **Phase 3:** edit a cost centre's actual % (authenticated, with a version token) → audit event written →
  a coalesced rebuild produces a new snapshot → the ranking updates; a stale-version write is rejected.
- **End-to-end:** dashboard drives estimate authoring → period open → monthly capture → watchlist → copilot
  on a freshly-imported project, all against Postgres.

---

## 9. Where each reconciliation rule is enforced (corrected)

| Rule | Enforced by |
|---|---|
| `ev / pv / earned_qty / resource_cost` correctness | **Generated columns** (row-local) |
| `cpi / eac / vac / alert / rolling` | **`cost_centre_evm` view** (guards, chains, calendar window) |
| Resource lines roll up to `boq_items.total_amount` | **Deferred constraint trigger / transactional finalization procedure** — *not* a CHECK (a `CHECK` can't hold a cross-row `SUM`, Finding 7); validate the complete BOQ after batch edits, block publishing an out-of-tolerance version, don't reject intermediate import rows |
| `period ↔ fact` keys | **`reporting_periods.id` + composite FK `(project_id, reporting_period_id)`**; fact `UNIQUE(project_id, cost_centre_id, reporting_period_id)` — no denormalized `bcc_id`/date in the key (Finding 4) |
| Cross-tenant referential integrity | **Composite FKs `(project_id, parent_id)`** (Finding 5) |
| Multi-project **disclosure** | **RLS on tables** (`FORCE`, transaction-local `app.current_user_id`+`app.current_project_id` validated pre-transaction, connection reset) + **`security_invoker` EVM view** (PG 15+; owner/app roles without `BYPASSRLS`; app doesn't own tenant tables) + **non-recursive membership policy** via an owner-safe function — composite FKs stop corrupt refs, RLS stops disclosure (Findings 6, 2–3 of 3rd review) |
| Estimate immutability for closed periods | **Version-scoped estimate graph** + on-fact baseline snapshot (Findings 1, 3, 10) |
| **Active-centre-period completeness + plan-curve monotonicity** | **Transactional validation at estimate publication and reporting-period close** (Finding 5 of 3rd review) — UNIQUE/FK/CHECK can't prove every active centre has a fact for every required period, nor that a curve never decreases. **Period close fails with a typed list/count of missing active-centre facts**; **publish fails on missing/duplicate/decreasing plan points or missing baseline coverage**; concurrency-tested so a fact can't vanish or a plan point change between validation and commit |
| Snapshot staleness | **`data_revision`** guard on rebuild activation (Finding 7) |
| Delete safety | **`RESTRICT`** on published estimate/baseline + closed history; soft-delete + authorized purge (Finding 11) |

## 10. Risks & resolved decisions (post-codex round 2)

- **The four frozen choices (§5.0)** resolve the round-2 blockers: on-fact baseline snapshot, cumulative→ledger
  cost storage, version-scoped estimate graph, RLS + Phase-2 auth.
- **Editing the estimate vs recorded history:** the fact snapshots baseline numerics at period open and
  references an immutable published version; closed periods never change when the estimate is re-authored.
- **Money & currency:** `NUMERIC`, neutral `*_amount`, immutable per-project reporting currency, **no
  cross-currency summing** without an explicit FX policy (Finding 8); no byte-for-byte claim (Finding 11).
- **Rolling CPI** uses a **calendar spine** with a declared missing-month policy that matches
  `FeatureBuilder`'s exact-predecessor rule, cross-tested with fixtures (Finding 5).
- **`alert_level` lifecycle** and **generated-column view-primary EVM** — unchanged and correct.
- **Data provenance:** single-project organiser data — the platform *enables* multi-project but doesn't by
  itself validate cross-project prediction.

## Codex Review

### Verdict

The product direction is sound, but the plan is not yet implementation-ready. The database migration is
described as a one-line loader swap while the implemented model lifecycle is single-project, synchronous,
singleton, and hard-coded to periods 1–12. The schema also leaves several tenant and accounting invariants
to application convention. Resolve the blocking findings below before Phase 0 migrations are written.

### Blocking findings

1. **The implemented analytics are not period- or project-dynamic.** `EvmThresholds` hard-codes
   `LastLabeledPeriod=11` and `ForecastPeriod=12`; `RollingOriginEvaluator`, controllers, and copilot tools
   use those constants directly. `ModelProvider` owns one `ModelSnapshot` and one source string. A project
   selector and future period 13 therefore cannot work through one DI change. Introduce a project-scoped
   model registry keyed by immutable `project_id`, derive each project's ordered reporting origins from the
   database, and make forecast origin = latest opened/closed reporting period rather than a compile-time
   constant. Define cache invalidation, concurrent rebuild de-duplication, last-known-good behavior, and
   bounded eviction before calling the platform multi-project.
2. **The proposed service lifetimes are invalid with EF Core.** The current `IPanelLoader` and
   `IModelProvider` are singletons. A normal `DbContext` is scoped and cannot safely be captured by either;
   it is also not thread-safe. Do not register a context-backed `PostgresPanelLoader` as the current
   singleton. Use `IDbContextFactory<QsDbContext>`/pooled factory inside a singleton registry, or make the
   complete read/rebuild operation scoped. Replace synchronous `Load`/`ToList()` with async,
   cancellation-aware methods and materialize a detached immutable snapshot before the context is disposed.
3. **Monthly actual-cost semantics are wrong or ambiguous.** In the source workbook the four resource
   amounts and `AC_AED_Period` are cumulative snapshots, despite their names. The plan calls them "monthly
   actual inputs" and makes `ac_total_aed` the row-local sum. If the UI captures monthly increments, CPI and
   EAC require a cumulative window/ledger and the generated column is wrong. Choose explicitly:
   - store cumulative-to-date resource balances on each period row, name them `ac_*_cumulative`, and enforce
     non-decreasing totals where appropriate; or
   - preferably store append-only cost transactions/period deltas and derive cumulative resource totals in
     a view before EVM calculation.
   Do not allow the UI/API to mix increments and cumulative balances.
4. **A reporting-period dimension is missing.** The PK
   `(project_id,bcc_id,period_id,period_start)` does not enforce `period_id ↔ period_start` one-to-one: both
   values can independently vary while the four-column tuple remains unique. A `BEFORE` trigger is a weak,
   concurrency-prone substitute. Add `reporting_periods(project_id, period_id, period_start, status,
   opened_at, closed_at)` with unique project-scoped keys, then reference it from facts. Use
   `(project_id,bcc_id,period_start)` as the fact uniqueness key and obtain the ordinal from the referenced
   period. This also provides the missing open/close workflow and dynamic model origins.
5. **Project isolation is not guaranteed by repeated `project_id` columns.** A child row can carry project A
   while referencing a parent surrogate ID from project B unless every relationship uses a composite FK
   such as `(project_id,parent_id) → parent(project_id,id)` backed by a matching unique constraint. Specify
   those FKs and index every referencing key. EF global query filters are developer guardrails, not a
   security boundary; add PostgreSQL RLS or equally strong repository authorization and set the tenant from
   authenticated request context. Authentication/authorization cannot wait until Phase 5 if write APIs
   arrive in Phases 3–4.
6. **The schema lacks a `cost_centres` master.** `bcc_id`, WBS/package assignment, discipline, unit, and
   lifecycle identity should not exist only as repeated period snapshots. Add a project-scoped
   `cost_centres` table with effective baseline/version references. Keep immutable baseline values on a
   period/rebaseline snapshot for historical EVM, but FK the fact to a stable cost-centre identity. Model
   `estimate_packages` (or another enforceable package entity) rather than relying on package-code text if
   package integrity is a claimed invariant.
7. **The cross-row reconciliation constraint is not implementable as written.** A PostgreSQL `CHECK`
   cannot contain a cross-row `SUM`, so "trigger + tolerance CHECK" cannot enforce resource-line rollup.
   Use a deferred constraint trigger or an explicit transactional validation/finalization procedure that
   checks the complete BOQ after batch edits. Store reconciliation state and prevent publishing/closing an
   estimate version that is outside tolerance; do not reject intermediate rows during a multi-row import.
8. **Import replacement is unsafe and inaccurately named.** PostgreSQL cannot `TRUNCATE` one project's rows;
   `TRUNCATE` affects a whole table. `DELETE WHERE project_id=...` is possible but delete-first replacement
   creates avoidable locking and operational risk. Import into staging tables under an `import_run`, validate
   row counts/FKs/reconciliation/computed EVM, then atomically activate the new estimate/baseline version.
   Preserve the previous active version for rollback and record source-file hash, importer version, actor,
   and timestamps.

### TimescaleDB decision

TimescaleDB is not justified by the stated volume (~2,000 rows/project) and makes the guaranteed Phase 0
dependent on an extension, raw migration SQL, chunk behavior, and deployment-specific version support.
Start with a regular PostgreSQL table plus indexes on `(project_id,bcc_id,period_start)` and
`(project_id,period_start)`; design the key so conversion remains possible if measured volume warrants it.

If Timescale remains mandatory, pin the PostgreSQL/Timescale versions and integration-test the exact DDL.
The claim that nothing may FK into a hypertable is outdated: current Timescale documentation says regular
tables may reference hypertables and hypertables may reference regular tables; the unsupported case is a
hypertable referencing another hypertable. The documented invariant that unique/primary indexes must
include every partitioning column is correct. See [Timescale constraints](https://docs.timescale.com/use-timescale/latest/schema-management/about-constraints/)
and [unique indexes](https://docs.timescale.com/use-timescale/latest/hypertables/hypertables-and-unique-indexes/).

### Required design corrections

9. **Move write safety ahead of CRUD.** Monthly facts and published estimate versions should not have
   unrestricted "full CRUD." Define draft/open/closed states, optimistic concurrency (`xmin` or an explicit
   version token), idempotency keys for submissions, close/reopen permissions, append-only audit events,
   and correction/reversal semantics. Audit and auth are prerequisites for Phase 3, not Phase 5 features.
10. **Make baseline versioning concrete.** "Snapshotted at period open" needs tables and transactions:
    `estimate_versions`, effective dates/status, `cost_centre_baselines`, the version referenced by each
    period, and an explicit rebaseline command. Specify what happens when a period is opened concurrently
    with estimate publication and prohibit silent mutation of baselines referenced by closed periods.
11. **Do not promise byte-for-byte reproduction across Excel/double/Postgres.** Money and percentages should
    use `NUMERIC` with declared precision/scale in PostgreSQL; the current domain uses `double`. Define
    rounding at the database/API boundary and verify field-specific tolerances. Reconciliation should compare
    CV/CPI/SPI/EAC/VAC and alert outcomes with documented tolerances, including zero denominators and
    sentinel/lifecycle cases—not one blanket `~0.5%` threshold.
12. **Rolling CPI needs calendar semantics.** `ROWS 2 PRECEDING` means the last three stored rows, not three
    consecutive reporting months. Join through `reporting_periods` and decide whether a missing month makes
    the rolling metric null, uses available observations, or inserts an explicit empty period. Match that
    policy in `FeatureBuilder`, which currently requires exact predecessor period IDs.
13. **Snapshot rebuilds must be transactionally consistent.** Read panel rows and the active baseline/version
    under one database snapshot, train outside the request path, then compare the source version before
    activation. A progress save should enqueue/coalesce a rebuild; it should not synchronously retrain and
    block the write response. Publish model metadata with `project_id`, source version/fingerprint,
    reporting cutoff, scorer version, build status, and failure reason.
14. **The concrete DDL is not actually present.** Before Phase 0, add migrations or an appendix specifying
    every column type, `NOT NULL`, `CHECK`, PK/unique/composite FK, delete behavior, and query-driven index.
    Use `NUMERIC` for money, `DATE` for reporting dates, `TIMESTAMPTZ` for events, `TEXT` plus lookup/checks
    for evolving workflow states, and explicit indexes on all FK/access paths. Avoid premature enums for
    business states that will evolve.

### Revised minimum viable sequence

1. Regular PostgreSQL schema: projects, reporting periods, cost centres, versioned baselines, progress
   facts, composite tenant FKs, migrations, and staging importer.
2. Import/reconciliation report proving the database view matches the workbook within field-specific
   tolerances.
3. Async project-aware snapshot/model registry and read-path swap; retain Excel as a comparison adapter.
4. Authenticated monthly capture with optimistic concurrency, audit, close/reopen rules, and asynchronous
   snapshot refresh.
5. Estimate authoring/version publication and explicit rebaseline workflow.
6. Add Timescale only after measured scale/query requirements justify it; add the challenger/copilot
   portfolio behavior after project isolation is proven.

The PostgreSQL table-design guidance materially changes the plan here: normalize stable identities and
versions first, enforce tenant relationships with composite foreign keys, index FK/access paths explicitly,
and use JSONB or denormalization only for measured needs. The current plan's additive reuse goal remains
valid, but the seam is a project-aware snapshot provider—not merely `PostgresPanelLoader`.

### Codex reconciliation — folded into the plan body

All 14 findings + the TimescaleDB decision are incorporated above. Map:

| # | Finding | Resolved in |
|---|---|---|
| 1 | Analytics not period/project-dynamic (hard-coded 4/11/12, one global snapshot) | §3 note, §5b (registry, origins from `reporting_periods`), §6 (Changed) |
| 2 | Scoped `DbContext` can't back a singleton loader; sync `Load` | §3 note, §5b (`IDbContextFactory` + async + detached snapshot) |
| 3 | Resource costs are **cumulative**, not monthly increments | §5 (cumulative-cost fix → `ac_*_cumulative` / delta ledger), §10 |
| 4 | Missing reporting-period dimension; `period_id↔period_start` not enforced | §5 (`reporting_periods` table), §9 (replaces `BEFORE` trigger) |
| 5 | `project_id` columns ≠ isolation; need composite FKs + RLS | §5 (composite FKs), §9, §5b (auth/RLS) |
| 6 | No `cost_centres` master / `estimate_packages` entity | §5 (both added) |
| 7 | Cross-row `SUM` can't be a `CHECK` | §9 (deferred constraint trigger / finalization procedure) |
| 8 | `TRUNCATE` can't scope to a project; delete-first unsafe | §5c (staging + `import_run` + atomic activation + rollback) |
| 9 | Write safety (auth/concurrency/audit/states) must precede CRUD | §7 (Phase 3), §10 |
| 10 | Baseline versioning needs concrete tables | §5 (`estimate_versions` + `cost_centre_baselines`), §10 |
| 11 | No byte-for-byte; use `NUMERIC`, field-specific tolerances | §5c, §5 (NUMERIC), §10 |
| 12 | Rolling CPI needs calendar semantics, not "last 3 rows" | §5 (calendar `RANGE` window via `reporting_periods`) |
| 13 | Snapshot rebuilds must be transactional + async + off request path | §7 (Phase 3 coalesced async refresh), §5b |
| 14 | Concrete DDL / types (`NUMERIC`/`DATE`/`TIMESTAMPTZ`, `TEXT`+CHECK for evolving states) | §5/§5b conventions; **full column-level DDL appendix is a Phase-0 deliverable** |

**Two calls flagged for your sign-off (they revise earlier decisions):**
1. **TimescaleDB deferred to Phase 5** (was "EF Core + TimescaleDB" from the earlier Q). Codex is right that
   ~2K rows/project doesn't justify the extension dependency in the guaranteed Phase 0. The key is designed
   to stay Timescale-convertible. *Re-enable now if you'd prefer to keep it — say so and I'll flip §5/§7 back.*
2. **"Full CRUD" gains workflow states** (draft/open/closed) + optimistic concurrency + audit + auth from
   Phase 3, rather than raw unrestricted CRUD. This honours your "full CRUD" choice but makes it safe; not a
   reduction in scope.

**Net:** the product direction and additive-reuse thesis stand; the correctness/tenancy/versioning
foundations are now specified. The one remaining pre-Phase-0 deliverable is the **column-level DDL appendix**
(every type, `NOT NULL`, CHECK, PK/unique/composite-FK, index) — I'll produce it as the first build step.

### Codex re-review — 2026-07-07

**Verdict:** the revision addresses the direction of all 14 findings, but it is still not ready for Phase 0.
Several items described as resolved remain mutually incompatible at the schema/transaction level. The DDL
appendix cannot be a mechanical first build step; the following decisions must be settled before it can be
written correctly.

#### Blocking findings

1. **Generated EVM columns cannot read the referenced baseline.** The fact now stores only `baseline_id`,
   while §5 says `ev`, `pv`, and `earned_qty_cumul` are row-local generated columns using BAC, budget
   quantity, and plan percent "from the referenced baseline." PostgreSQL generated expressions cannot query
   another table. Choose one consistent design:
   - snapshot the required baseline numerics (`bac`, `budget_qty`, `plan_pct_complete`) onto the period fact
     and generate row-local values from those immutable columns; or
   - keep only `baseline_id` and calculate PV/EV/earned quantity in `cost_centre_evm` by joining the immutable
     baseline table.
   The second is more normalized; the first makes the historical snapshot explicit. Do not claim generated
   columns are unbypassable while their inputs live in a different row.
2. **Actual-cost storage is still an unresolved fork.** "Store cumulative values — or, preferred, an
   append-only ledger" describes two materially different schemas, APIs, correction rules, and EVM views.
   Select one before Phase 0. For a digital system of record, use an append-only `cost_transactions` or
   `period_cost_deltas` ledger with resource type, amount, effective period, posting/reversal identity, and
   idempotency key; derive cumulative AC. If cumulative snapshots are chosen for the hackathon, explicitly
   mark them as an import-compatible interim model and define correction/reclassification behavior rather
   than assuming every resource balance is monotonic.
3. **Estimate versioning does not yet version the estimate graph.** Adding `estimate_versions` and
   `cost_centre_baselines` is insufficient if `norms`, `boq_items`, mappings, packages, and resource lines
   remain project-global mutable rows. Editing those rows rewrites the source of an older published estimate,
   and staging a second import collides with project-scoped unique keys. Make every authored estimate entity
   owned by an `estimate_version_id` (with version-scoped uniqueness), or model immutable revision rows plus
   an active-version pointer. Publication atomically validates and activates a complete graph; published
   versions are immutable. Only then can staging activation and rollback work as claimed.
4. **The reporting/fact keys are internally inconsistent.** The fact is described as carrying
   `cost_centre_id`, but its uniqueness is still `(project_id,bcc_id,period_start)`. It also says
   `period_start` references `reporting_periods`, although the proposed period table has two separate unique
   keys. Freeze explicit keys: for example, `reporting_periods.id` plus `UNIQUE(project_id,id)` and
   `UNIQUE(project_id,period_id)`, `UNIQUE(project_id,period_start)`; then the fact uses composite FKs
   `(project_id,reporting_period_id)` and `(project_id,cost_centre_id)` with
   `UNIQUE(project_id,cost_centre_id,reporting_period_id)`. Do not retain denormalized `bcc_id`/date in the
   uniqueness contract unless they are deliberately stored and constrained.
5. **The calendar rolling window does not implement the stated missing-month policy.** Joining facts to
   `reporting_periods` and using `RANGE INTERVAL '2 months' PRECEDING` still averages available fact rows; it
   neither creates absent cost-centre/month rows nor makes the result null. Build a calendar spine of expected
   `(cost_centre, reporting_period)` rows (or require one fact snapshot per active centre per open/closed
   period), then explicitly require three consecutive observations or define available-observation behavior.
   Add fixtures for a missing middle month and lifecycle transitions so SQL rolling CPI and C# exact lags
   agree.
6. **Tenant security still starts too late and remains an option.** Phase 2 exposes project-selectable
   watchlist/copilot reads, but authentication appears in Phase 3 and §5b still says "RLS or repository
   authorization." Select the security boundary now. Authenticate and authorize project membership before
   the first multi-project read endpoint. If using RLS, define transaction-local tenant context, enable and
   force RLS for the application role, ensure pooled connections reset state, and test cross-tenant reads
   and writes. Composite FKs prevent corrupt references; they do not prevent data disclosure.
7. **Snapshot consistency Finding 13 is not fully specified.** Async/coalesced rebuild describes scheduling,
   not consistency. Define a project `data_revision` incremented in the same transaction as every relevant
   write. A rebuild reads under one transaction snapshot, records the revision, trains after materialization,
   then activates only if the current revision still matches; otherwise it retries/coalesces. Persist build
   status, source revision/fingerprint, reporting cutoff, scorer version, timestamps, and failure reason.
   Reads must return the active snapshot's source revision so staleness is observable.
8. **Multi-currency naming contradicts the project model.** `projects.currency` implies multiple currencies,
   while schema examples retain `_aed` columns and the existing domain/API assumes AED. Store neutral
   `*_amount` values with the project's immutable reporting currency (or snapshot currency on estimate
   versions). Prohibit changing currency after monetary data exists. Portfolio totals require an explicit FX
   policy; otherwise display per-currency groups and never sum unlike currencies.

#### Required corrections to the revised plan

9. Change §6 from "Reused unchanged" to an honest compatibility map. `Domain` projections may remain, but
   `ModelProvider`, `EvmThresholds`, evaluator inputs, controllers, copilot tools, DTOs/routes, and frontend
   state all change for project-aware dynamic periods. Add regression tests for period 13+, projects with
   fewer than four origins, independent project cutoffs, cache eviction, and simultaneous rebuilds.
10. Move authentication/project authorization into Phase 2. Phase 3 should add write-specific permissions,
    concurrency, audit, and close/reopen workflow—not establish the first tenant boundary.
11. Define delete behavior before migrations: use `RESTRICT` for published estimate/baseline and closed-period
    history; avoid blanket tenant-root `ON DELETE CASCADE` for auditable financial records. Prefer project
    archival/soft deletion, with a separately authorized purge workflow.
12. Make Phase 0 end with reviewed, executable DDL and Testcontainers migration tests—not merely promise an
    appendix. Tests must prove composite tenant FKs, version immutability, unique period/fact keys, numeric
    rounding/checks, publish finalization, staging rollback, RLS isolation (if selected), and query indexes.

#### Revised readiness gate

The next plan revision should freeze four choices first: **baseline projection**, **cost ledger vs cumulative
snapshot**, **version ownership of the full estimate graph**, and **RLS vs application authorization**.
After those choices, write the exact DDL and transactional workflows, validate them in PostgreSQL, and only
then begin the importer. The PostgreSQL skill guidance reinforces this sequencing: normalize immutable
identities/versions, make every tenant relationship enforceable, add indexes for actual FK/query paths, and
denormalize snapshots only when the historical contract explicitly requires them.

### Codex round-2 reconciliation — folded into the plan body

The **four demanded choices are now frozen in §5.0**, and all 8 blocking findings + 4 corrections are
incorporated. Map:

| # | Re-review finding | Frozen choice / resolution | Where |
|---|---|---|---|
| 1 | Generated EVM can't read the baseline table | **Choice 1:** snapshot `bac/budget_qty/plan%` onto the fact; generate row-locally | §5.0, §5.1 fact table |
| 2 | Cost storage fork (cumulative vs ledger) | **Choice 2:** cumulative interim (import/Phases 0–2) → append-only `period_cost_deltas` ledger at Phase 3 | §5.0, §7 |
| 3 | Versioning doesn't version the estimate *graph* | **Choice 3:** `estimate_version_id` ownership, version-scoped uniqueness, active-version pointer, atomic publish, immutable published | §5.0, §5.1 |
| 4 | Reporting/fact keys inconsistent | frozen keys: `reporting_periods.id` + composite FK `(project_id, reporting_period_id)`; fact `UNIQUE(project_id, cost_centre_id, reporting_period_id)` | §5.1, §9 |
| 5 | Rolling window ignores missing months | calendar spine + "3 consecutive present else null" policy matching `FeatureBuilder`; cross-tested fixtures | §5 rolling, §10 |
| 6 | Tenant security starts too late / optional | **Choice 4:** RLS boundary + auth in **Phase 2** | §5.0, §5b, §7 |
| 7 | Snapshot consistency underspecified | `data_revision` incremented per write; rebuild activates only if revision matches; staleness observable | §5b |
| 8 | Multi-currency naming contradiction | neutral `*_amount`, immutable project reporting currency, no cross-currency sums | §5.1, §10 |
| 9 | "Reused unchanged" dishonest | honest compatibility map + regression tests (period 13+, <4 origins, eviction, races) | §6 |
| 10 | Auth in Phase 2, not 3 | moved to Phase 2 | §7 |
| 11 | Delete behaviour | `RESTRICT` on financial/closed rows; soft-delete + authorized purge | §5.1, §9 |
| 12 | Phase 0 ends with real DDL + tests | Phase 0 exit gate = executable DDL + Testcontainers migration tests | §7 |

**Flagged for your veto** (choices that set architecture, not just wording):
1. **On-fact baseline snapshot** (vs join-in-view) — chosen for immutable history + valid generated columns.
2. **Cumulative-now / ledger-at-Phase-3** — chosen because the workbook is cumulative but live capture needs a ledger.
3. **Version-scoped estimate graph** — the biggest schema consequence; makes published estimates truly immutable.
4. **RLS as the tenant boundary** — DB-enforced isolation over app-only checks.

**Status:** the four foundational choices are frozen and the contradictions are resolved, so the next step is
codex's readiness gate — **write the exact column-level DDL + transactional workflows and prove them in
PostgreSQL via Testcontainers** (Phase 0). That is the first build task, not more planning. Say the word and
I'll produce the DDL + migration tests; or push back on any of the four choices first.

### Codex second re-review — 2026-07-07

**Verdict:** the eight prior blockers and four readiness choices are now addressed at the architectural
level. Five schema/workflow details still need correction before the DDL can be written without rework.

1. **`plan_pct_complete` is period-specific, not a per-cost-centre baseline attribute.** BAC and budget
   quantity are stable for a published estimate version, but planned completion is a time-phased schedule
   value that changes each reporting period. A single `cost_centre_baselines` row cannot correctly own it.
   Add a versioned `cost_centre_plan_periods` table keyed by
   `(project_id, estimate_version_id, cost_centre_id, reporting_period_id)` with planned percent/quantity.
   At period open, copy that period's plan value plus BAC/budget quantity onto the fact, in one transaction.
   Validate plan percent bounds and define whether the curve must be non-decreasing and end at 100%.
2. **The cumulative-to-ledger cutover is undefined.** Phase 0 imports cumulative resource balances into
   fact columns, while Phase 3 makes `period_cost_deltas` canonical. Define a one-time migration that converts
   consecutive cumulative snapshots into opening balances/deltas per resource, handling the first period,
   missing periods, negative corrections, and reclassifications. After cutover, stop accepting writes to
   cumulative fact columns and make `cost_centre_evm` read one canonical ledger-derived cumulative view.
   Do not leave two writable sources of AC or choose between them with ad-hoc SQL.
3. **RLS needs identities, policies, and background-worker semantics.** Add
   `project_memberships(user_id, project_id, role)` (or the external-identity equivalent) and state the
   policy predicate. `SET LOCAL app.current_project` alone proves only that a caller supplied a project ID;
   it does not prove membership. The async snapshot registry/importer has no authenticated HTTP request, so
   define a separate least-privilege worker role and explicitly set the target project inside its transaction;
   reserve a tightly controlled bypass role for migrations/purge only. Add tests for spoofed project context,
   missing context, pooled-connection reuse, worker access, and membership revocation.
4. **The calendar spine cannot infer "active" periods from a missing fact.** Lifecycle currently lives on
   the fact row, so when a row is absent the query cannot know whether it represents missing data, not-started,
   or closed work. Add effective start/end periods (or a separate lifecycle-history table) for each cost
   centre, then generate expected centre-period rows from that contract. Alternatively require a fact row
   for every centre-period and make absence a data-contract violation. Freeze one approach; only then can
   "three consecutive present observations else null" be implemented and tested reliably.
5. **Snapshot columns are not immutable merely because they are copied.** Define database enforcement that
   prevents BAC/budget/plan snapshot updates once a reporting period is closed, and prevents arbitrary edits
   even while open except through the authorized open/rebaseline transaction. Generated PV/EV values will
   silently change if those inputs remain generally updateable. The rebaseline command must write an audit
   event, identify the old/new estimate versions, update only permitted open/future periods, increment
   `data_revision`, and never mutate closed facts.

#### DDL readiness additions

- Include `cost_centre_plan_periods`, `project_memberships`, the selected lifecycle expectation model, and
  ledger cutover/backfill tables or migration SQL in the Phase-0 DDL package.
- Specify RLS policies for every tenant table and view. Ensure security-barrier behavior/ownership does not
  let the EVM view bypass intended policies; integration-test the view as the application role.
- Add composite FK indexes explicitly, including leading-column order matching project-scoped joins.
- Add transaction tests for period-open snapshotting, concurrent estimate publication vs period open,
  ledger cutover reconciliation, closed-period mutation rejection, and rebaseline audit/revision updates.

Once these five contracts are frozen, producing executable DDL and Testcontainers tests is the correct next
step. The PostgreSQL skill's normalization guidance is especially relevant to separating the time-phased
plan curve from the stable cost-centre baseline and maintaining one canonical cost ledger after cutover.

### Codex round-3 reconciliation — folded into the plan body

Codex confirmed the architecture is sound; the five remaining schema/workflow contracts are now frozen:

| # | Second re-review finding | Resolution | Where |
|---|---|---|---|
| 1r | `plan_pct_complete` is period-specific, not a baseline attribute | split: `cost_centre_baselines` (stable `bac`/`budget_qty`) vs new **`cost_centre_plan_periods`** (time-phased curve, non-decreasing→100%); period-open copies that period's `planned_pct` onto the fact | §5.0 Choice 1, §5.1 |
| 2r | Cumulative→ledger cutover undefined | defined one-time migration (cumulative → opening balance + deltas; first/missing/negative/reclass handled); post-cutover cumulative columns read-only, **one canonical** ledger-derived AC view | §5.0 Choice 2, §5.1, §7 |
| 3r | RLS needs identities, policies, worker semantics | **`project_memberships`** + membership policy predicate; least-privilege **worker role** for registry/importer; bypass role for migrations/purge; policies on every table **and view**; revocation/spoof tests | §5.0 Choice 4, §5.1, §7 |
| 4r | Spine can't infer "active" from a missing row | **`cost_centres.effective_start/end`** + **one-row-per-active-centre-period** contract (absence = violation); spine built from that | §5 rolling, §5.1 |
| 5r | Snapshot columns aren't immutable just by copying | **trigger-enforced** immutability on closed periods + open-only-via-authorized-transaction; rebaseline writes audit, old/new version, open/future only, `data_revision`++, never mutates closed facts | §5.0 Choice 1, §7 |

**Status:** codex's verdict is that the architecture is sound and, with these five contracts frozen,
**"producing executable DDL and Testcontainers tests is the correct next step."** The plan has reached its
readiness gate. No architectural questions remain open — the next action is the **Phase-0 DDL package + the
migration/transaction test suite**, not further planning.

Two of the round-3 resolutions add tables you may want to eyeball (both are low-controversy normalizations):
`cost_centre_plan_periods` (separating the S-curve from the baseline) and `project_memberships` (the identity
behind RLS). Say the word and I'll write the Phase-0 DDL + Testcontainers tests; that is now the build task.

### Codex third re-review — 2026-07-07

**Verdict:** the five second-review contracts are now represented, but one proposed constraint contradicts
the workbook and three enforcement mechanisms need PostgreSQL-specific correction before DDL.

1. **Do not require every plan curve to end at 100% within the imported horizon.** The supplied workbook has
   non-decreasing plan curves, but **166 of 173 centres end period 12 below 100%** because the project is
   still underway (examples include centres ending at 73–99%). A blanket "ends at 100%" publication/import
   rule would reject valid source data. Enforce `0 <= planned_pct <= 100` and non-decreasing values across
   consecutive planned periods. Require 100% only when the version's declared schedule horizon contains the
   centre's planned-finish period or when the centre is explicitly marked plan-complete—not at the latest
   currently imported reporting period. Store one source of truth (`planned_pct` or `planned_qty`) and derive
   the other, or validate their equality during publication.
2. **PostgreSQL RLS policies do not attach to ordinary views.** The plan says to define RLS on every table
   "and view," but RLS is enforced on tables; a view may execute with its owner's privileges and bypass
   underlying RLS. Pin a PostgreSQL version and define `cost_centre_evm` as a security-invoker view where
   supported (PostgreSQL 15+: `WITH (security_invoker = true)`), or expose a carefully reviewed invoker-safe
   function/query. The view owner and application role must not have `BYPASSRLS`, and the app must not own
   tenant tables. Test the EVM view itself for cross-project leakage as both the app and worker roles.
3. **The membership policy needs a non-recursive identity design.** Applying the same membership-based RLS
   predicate to `project_memberships` can recurse when policies on other tables query it. Define both
   transaction-local user identity and selected project (`app.current_user_id`, `app.current_project_id`),
   validate them against trusted authentication before opening the transaction, and implement membership
   lookup through a narrowly scoped, owner-safe function or a non-recursive policy on the membership table.
   Test a user belonging to multiple projects, removed membership, a spoofed user/project setting, and no
   setting. Do not rely on a caller-set project GUC alone.
4. **A trigger cannot securely recognize an "authorized transaction" by convention.** If the application
   role can directly update snapshot columns, it can invoke the same SQL path or spoof a custom flag used by
   the trigger. Revoke direct `UPDATE` on BAC/budget/plan snapshot columns from app/worker roles and expose
   period-open/rebaseline through explicit transactional procedures or a narrowly scoped service role.
   Keep a trigger as defense-in-depth for closed-period rejection, but make privileges/procedure ownership
   the authorization boundary. Security-definer code must use a fixed safe `search_path` and fully qualified
   objects.
5. **Completeness and curve monotonicity are cross-row rules and need a finalization point.** UNIQUE/FK/CHECK
   constraints cannot prove that every active centre has a fact for every required reporting period or that
   a plan curve never decreases. Add transactional validation to estimate publication and reporting-period
   close. Closing a period must fail with a typed list/count of missing active-centre facts; publishing a
   version must fail on missing/duplicate/decreasing plan points or missing baseline coverage. Concurrency
   tests must prove a fact cannot disappear or a plan change between validation and commit.

#### Readiness update

Add the workbook-backed plan-curve regression test (`166/173` incomplete at period 12), security-invoker
view tests, non-recursive membership-policy tests, privilege/procedure tests, and publication/period-close
completeness tests to the Phase-0 exit gate. With those corrections, the plan can proceed to exact DDL and
transaction implementation; without them, the first migration would either reject Tower X or expose a
tenant-bypass path.

### Codex round-4 reconciliation — folded into the plan body

All five third-review corrections are now incorporated. None required a new architectural choice — each
tightens an existing contract to be workbook-accurate and PostgreSQL-correct. Map:

| # | Third-review correction | Resolution | Where |
|---|---|---|---|
| 1 | "Ends at 100%" would reject 166/173 Tower X centres | dropped the blanket rule; enforce `0 ≤ planned_pct ≤ 100` + non-decreasing across consecutive planned periods; require 100% only when the schedule horizon reaches planned-finish or the centre is marked plan-complete; one stored source of truth (`planned_pct`/`planned_qty`), other derived/equality-validated | §5.0 Choice 1, §5.1 |
| 2 | RLS does not attach to ordinary views | pin PG 15+; `cost_centre_evm` as `security_invoker`; owner/app roles without `BYPASSRLS`; app doesn't own tenant tables; view leakage tested as app + worker roles | §5.0 Choice 4, §5.1 EVM view, §7, §9 |
| 3 | Membership policy can recurse | two validated settings (`app.current_user_id` + `app.current_project_id`) checked pre-transaction; membership resolved via an owner-safe `SECURITY DEFINER` function; `project_memberships` carries a non-recursive policy keyed on `app.current_user_id`; **§5b made consistent (4th review): both settings everywhere; worker is an RLS-governed service principal with its own `project_memberships` entry, no `BYPASSRLS`, owns no tenant tables** | §5.0 Choice 4, §5.1, §5b |
| 4 | A trigger can't recognize "authorized" by convention | revoke direct `UPDATE` on snapshot columns from app/worker; period-open/rebaseline only via `SECURITY DEFINER` procedures (fixed `search_path`, fully-qualified objects); trigger kept as defense-in-depth for closed periods | §5.0 Choice 1, §5.1 fact table, §7 |
| 5 | Completeness/monotonicity are cross-row rules needing a finalization point | transactional validation at publication + period-close; close fails with a typed list/count of missing active-centre facts; publish fails on missing/duplicate/decreasing plan points or missing baseline coverage; concurrency-tested | §7, §9 |

**Status:** codex's 4th review confirmed corrections 1, 2, 4, and 5 sufficient and flagged one leftover
inconsistency — §5b still used the single `app.current_project` setting and left the worker's identity/
membership path undefined. That is now fixed (§5b: both `app.current_user_id`+`app.current_project_id`
everywhere; the worker is an RLS-governed service principal with its own `project_memberships` entry, no
`BYPASSRLS`, owning no tenant tables). With that, the plan carries no known contradiction against the workbook
or PostgreSQL semantics. **Codex re-reviewed the §5b fix and confirmed: "Finding 3 is fully resolved. The plan
is sufficient to proceed with the Phase-0 executable DDL and Testcontainers migration/transaction suite."**
No blocking findings remain across four codex review rounds. The next action is the **Phase-0 executable DDL
package + Testcontainers migration/transaction suite** (including the third-review regression/security/
completeness tests) — not further planning.
