# Phase 0 — Postgres system-of-record DDL + contract gate

This is the first build step of `plan/digitalization-postgres-platform-plan.md`: the **reviewed,
executable DDL package + a proven contract suite**, which the plan (and four codex review rounds)
named as the readiness gate before anything else is built.

Everything here is raw SQL rather than EF Core model migrations on purpose — RLS policies, generated
columns, `security_invoker` views, `SECURITY DEFINER` procedures and triggers are the substance of
Phase 0 and don't round-trip through EF migrations. EF Core maps onto this schema in Phase 2.

## Layout

```
db/
  migrations/
    0001_roles.sql              role separation: qs_owner / qs_app / qs_worker / qs_bypass
    0002_schema.sql             all tenant tables, composite FKs, generated columns, CHECKs, indexes
    0003_evm_view.sql           cost_centre_evm (WITH security_invoker = true)
    0004_rls.sql                non-recursive membership fn + FORCE RLS + policies + column grants
    0005_procedures_triggers.sql  period-open / rebaseline / close / publish + validation + immutability
  tests/
    seed.sql                    deterministic two-project fixture
    test_contracts.sql          the behavioral gate (ASSERT-based)
  apply.sh                      apply migrations in order to a database (PG15+ enforced)
  run_tests.sh                  recreate a throwaway db, apply, seed, run the contract suite
```

## Run it

Requires a reachable PostgreSQL **15+** (`security_invoker` views). Uses `PGHOST`/`PGPORT`/`PGUSER`
(a superuser or CREATEROLE role, because `0001` creates roles).

```bash
cd QsEarlyWarning/db
./run_tests.sh                 # recreates qs_phase0_test, applies, seeds, asserts → "PHASE-0 GATE: PASS"
./apply.sh my_database         # just apply the schema to an existing database
```

CI parity: `tests/QsEarlyWarning.Db.Tests` runs the *same* SQL inside a `postgres:17` Testcontainer
(`dotnet test`), so the gate is enforced in CI. It soft-skips when no Docker daemon is present.

## What the gate proves (maps to the codex findings)

| Test | Contract | Finding |
|------|----------|---------|
| T1 | publish validates coverage/monotonicity/rollup; a plan curve ending **< 100%** still publishes | 3rd-review 1 |
| T2 | a **decreasing** plan curve blocks publish | 3rd-review 5 |
| T3 | period-open snapshots `bac`/`budget_qty`/`planned_pct` onto one fact per active centre; generated PV/EV are row-local and correct | Choice 1 |
| T4 | `qs_app` **cannot** UPDATE snapshot columns (privilege-denied), **can** update actuals on an open period | 3rd-review 4 |
| T5a | period close fails with a **typed list** of missing active-centre facts | 3rd-review 5 |
| T5b–d | complete close succeeds; closed-period facts are frozen (trigger); rebaseline rejected on closed periods | Choice 1 / 3rd-review 4 |
| T6 | RLS isolation: member / spoofed-project / no-context / multi-project / worker service principal | Findings 5,6 / 3rd-review 3 |
| T7 | the `security_invoker` EVM view enforces the querying role's RLS (no cross-tenant leak) | 3rd-review 2 |
| T8 | `project_memberships` visibility is per-user and **non-recursive** | 3rd-review 3 |

## Design notes worth knowing

- **Tenant boundary is table RLS + a security_invoker view.** Every tenant table is
  `ENABLE`+`FORCE ROW LEVEL SECURITY`, owned by `qs_owner` (never by the app/worker roles).
  Identity is two transaction-local settings, `app.current_user_id` and `app.current_project_id`,
  both meant to be validated by the app *before* the transaction opens.
- **Membership resolution is non-recursive.** `qs.fn_is_member` is `SECURITY DEFINER` with a fixed
  `search_path`; `project_memberships` carries a policy keyed only on `app.current_user_id`, so a
  tenant policy that calls `fn_is_member` cannot recurse back through it.
- **Snapshot immutability is a privilege, not a convention.** App/worker hold column-level UPDATE on
  the actual-input columns only; `bac`/`budget_qty`/`planned_pct` are writable exclusively through
  the `SECURITY DEFINER` period-open / rebaseline procedures. The closed-period trigger is
  defense-in-depth.
- **Cross-row rules are validated transactionally**, not as CHECKs: BOQ rollup, plan-curve
  monotonicity, baseline coverage, and the horizon-aware 100% rule at publish; active-centre-period
  completeness at period close (fails with an enumerated list).

## Phase 1 — Excel importer + EVM reconciliation (done)

`src/QsEarlyWarning.Infrastructure/Import/WorkbookImporter.cs` loads `9_HISTORICAL_DATA` into this
schema and proves the thesis: the withheld EVM columns are **derivable** from the authored inputs.

Run it (schema must already be applied to the target db):

```bash
# create + apply, then import
psql -d postgres -c 'CREATE DATABASE qs_phase1;'
db/apply.sh qs_phase1
dotnet run --project tools/QsEarlyWarning.Importer         # defaults: data/Tower_X_Project_Data.xlsx → tower-x
```

What it does (all in one transaction, as the `qs_bypass` backfill role):
1. purge any prior load of the slug, then insert project → reporting_periods → estimate_version →
   cost_centres → baselines → plan_periods (**inputs only — nothing computed is derived**);
2. run the Phase-0 publish validation (`fn_validate_publish`) and **activate** the version;
3. bulk-load 2,076 facts (BAC/plan%/actual%/AC splits/lifecycle);
4. reconcile the DB-computed `cost_centre_evm` against the workbook's recorded columns.

Result on Tower X: **CPI, SPI, CV, EAC, VAC, %-budget-consumed and alert all reconcile 100%** within
field-specific tolerances. The report also, honestly:
- **excludes 9 rows** where the workbook's *own* recorded EV/PV contradict its inputs
  (e.g. `Actual%=1`, BAC=24,114 but recorded EV=24,114 — a source error, listed with evidence); and
- **excuses 3 rows** whose CPI sits within rounding distance of the 0.95 AMBER cutoff (label
  indeterminate at the boundary).

Provenance is recorded in `qs.import_runs` (source hash, importer version, actor, row counts, status).

## Phase 2 — RLS-scoped read path + Excel↔Postgres parity (done)

The analytics stack reads `IReadOnlyList<CostCentrePeriod>`; Phase 2 serves that list from Postgres
through the RLS boundary instead of the Excel file, and proves the swap is behavior-preserving.

New pieces:
- `Infrastructure/Postgres/PostgresPanelLoader.cs` — async, pooled (`NpgsqlDataSource`). Each read
  assumes the `qs_app` role and sets transaction-local `app.current_user_id` + `app.current_project_id`
  before touching a tenant table, so the `security_invoker` EVM view enforces RLS as the caller.
- `Core/Registry/ProjectSnapshotRegistry.cs` — project-keyed, async, thread-safe registry that
  replaces the single-global `ModelProvider`: materializes a detached panel + trained model, caches
  by immutable `project_id`, de-dups concurrent rebuilds, keeps last-known-good, and derives the
  forecast origin from the DB (latest present period, not a constant 12).

Prove it (after importing `tower-x`):

```bash
dotnet run --project tools/QsEarlyWarning.Importer -- verify
```

Result on Tower X: for **all 9 scored periods, the Postgres read path flags the identical set of
top-5 and top-10 at-risk centres as the Excel adapter** (9/9). Exact ordering matches 7/9; the two
reorderings are near-ties (|Δscore| ≈ 0.0001) between computed EVM and the workbook's rounded
recorded EVM — the same centres, negligibly different intra-tie order. The watchlist rule scores on
`gap` and `cpi` only, both of which reconcile 100% in Phase 1.

**Dynamic origins (codex Finding 1) — done.** `RollingOriginEvaluator` no longer hard-codes periods
4/11/12: `ReportingOrigins.FromPanel` derives `FirstOrigin` / `LastLabeledPeriod` / `ForecastPeriod`
from the data and the model exposes them. Tower X is byte-identical (34/34 unit tests green); a
shifted-period test proves a panel over periods 5..16 forecasts at 16, not 12.

## Phase 2c — API served from Postgres behind RLS + per-request authorization (done)

The `GET /api/v1/watchlist` endpoint now serves from the project-aware registry, not the Excel file.
A `TenantContext` middleware reads `X-User-Id` + `X-Project-Slug` (a stand-in for a validated token;
the *authorization* half is real — enforced by RLS), the `ProjectResolver` maps slug→id, and the
controller validates the period against the model's **DB-derived** origins.

A subtle tenancy bug was found and fixed during this step: the snapshot cache is keyed by project, so
a cache hit could serve a non-member another user's snapshot. Authorization is now a **per-request**
RLS probe (`IProjectPanelSource.IsAuthorizedAsync` → `SELECT EXISTS(SELECT 1 FROM qs.projects)` as the
app role), independent of the data cache.

Proven against the live API (`--urls http://localhost:5199`, db `qs_phase1`):

| request | result |
|---|---|
| no identity headers | **401** |
| member (user 1 / tower-x), period 8, k=5 | **200** — top-5 led by `BCC-ARC-PAINT-317` (the plan's named centre) |
| non-member (user 2 / tower-x) | **403** |
| unknown project | **404** |
| out-of-range period 99 | **400** |

Deferred to **Phase 2d**: the copilot tool answering over live Postgres data (it + `HealthController`
still use the single-project Excel `ModelProvider`); real token/OIDC auth in place of the header shim.

## Phase 3 — append-only cost ledger + cutover (done)

`0007_cost_ledger.sql` adds the live-capture model (plan §5.0 Choice 2). Actual cost is resolved once
in the EVM view (`ac_eff`): the on-fact cumulative snapshot before cutover, the ledger-derived
cumulative after. A non-cutover project (Tower X) is byte-identical — Phase-1 reconciliation and
Phase-2 parity still PASS.

- `sp_post_cost_delta(...)` — capture: append-only, **idempotent** by key, membership-checked,
  rejected on closed periods, bumps `data_revision`.
- `sp_cutover_to_ledger(project)` — one-time migration: cumulative fact snapshots → per-resource,
  per-period signed deltas (opening balance + increments), flips `projects.ledger_active`.
- After cutover the fact `ac_*` columns are frozen (trigger) and the ledger is append-only
  (UPDATE/DELETE revoked from app/worker).

`db/run_tests.sh` now also runs `tests/test_ledger.sql`, proving: cutover produces the right deltas;
the ledger-derived EVM AC reconciles to the pre-cutover totals (100/250/400) with CPI intact;
idempotent re-posting; append-only enforcement; frozen fact columns; closed-period rejection.
(`data_revision` is bumped on every capture — the async coalesced snapshot refresh on save is the
remaining app-layer wiring, using the existing `ProjectSnapshotRegistry.RebuildAsync`.)

## Phase 4 — estimate authoring, atomic publication, published-version immutability (done)

Most of the machinery was built in Phase 0 (`sp_publish_estimate_version` with cross-row validation,
`sp_rebaseline_period`, version-scoped uniqueness, draft/published/superseded). `0008` adds the
missing guarantee: **a published (or superseded) estimate's authored graph is immutable** — a trigger
on all eight authored tables rejects INSERT/UPDATE/DELETE unless the version is still `draft`
(superuser/bypass exempt for migration/purge). To change a published estimate you author a new draft
and publish it, which supersedes the old one and re-points `projects.active_estimate_version_id`.

`tests/test_authoring.sql` proves: publish activates v1 → editing v1's graph is rejected → editing
draft v2 is allowed → publishing v2 supersedes v1 and repoints active → editing the superseded v1 is
still rejected.

## Phase 5 — scale / portfolio (assessed; currency integrity enforced)

- **TimescaleDB: deferred**, per the plan and codex — ~2K rows/project doesn't justify the extension
  dependency; the key is designed to stay convertible if measured volume ever warrants it.
- **Cross-project isolation: already proven** by the RLS suite (`test_contracts` T6 — member, spoof,
  no-context, multi-project user, worker service principal).
- **Currency / FX integrity: enforced + tested** (`tests/test_portfolio.sql`). Money is per-project in
  an **immutable reporting currency** (settable only before monetary data exists), so unlike
  currencies can never be blind-summed. A true portfolio rollup with an explicit FX policy is future
  work that needs real multi-currency projects.

## Status — all plan phases addressed

| Phase | What | Proof |
|-------|------|-------|
| 0 | DDL + RLS + procedures + immutability | `PHASE-0 GATE: PASS` (Testcontainers/psql) |
| 1 | Excel importer + EVM reconciliation | importer `VERDICT: PASS` (100% derivable) |
| 2 / 2b / 2c | RLS read path, dynamic origins, API served from Postgres + per-request authz | parity `PASS`; 34/34 unit tests; live-API 401/403/404/400/200 |
| 3 | append-only cost ledger + cutover | `ALL LEDGER TESTS PASSED` |
| 4 | authoring + publication + immutability | `ALL AUTHORING TESTS PASSED` |
| 5 | currency integrity; scale/portfolio assessed | `ALL PORTFOLIO TESTS PASSED` |

Remaining app-layer polish (not blocking): copilot over live data, async coalesced refresh-on-save,
real token auth, and a currency-aware portfolio rollup.
