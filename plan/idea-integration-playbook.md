# Playbook — integrate an `ideas/idea-N-*.md` into the QS platform

A reusable prompt. To integrate a new idea, paste the **PROMPT** block below into Claude Code with
`{{IDEA_FILE}}` replaced by the idea's path (e.g. `ideas/idea-1-early-warning-classifier.md`). It
reproduces the exact workflow used for Idea 2 (`plan/idea-2-eac-forecaster-integration-plan.md` +
commit `924ab38`): explore → scope → plan → codex-review loop → implement → verify → ship.

> Tip: the codex-review loop step is best run with `/loop` (see step 4). Everything else runs inline.

---

## PROMPT (paste this; set `{{IDEA_FILE}}`)

You are integrating **{{IDEA_FILE}}** into the existing QS system-of-record platform. Follow this
workflow exactly. Prefer **reusing** the platform's patterns over inventing new ones. Be honest in
verification — report what the numbers actually show; never assert a win the data doesn't support.

### Platform conventions you MUST reuse (don't rebuild)
- **Stack:** ASP.NET Core 8 Web.API + `QsEarlyWarning.Core` (analytics, no ML deps) + `QsEarlyWarning.Infrastructure`
  (raw **Npgsql**, no EF Core) + React/Vite frontend. PostgreSQL 17 local DB `qs_phase1`.
- **Data access:** the project **snapshot** — `ProjectSnapshotRegistry.GetOrBuildAsync(projectId,userId)`
  returns a `ProjectSnapshot { Panel: IReadOnlyList<CostCentrePeriod>, Model, MinPeriod, ForecastPeriod, … }`.
  New per-project computed artifacts are **fit in `ProjectSnapshotRegistry.Build()`** and hung on the
  snapshot (degrade gracefully in a try/catch so a failure doesn't sink the snapshot). No new DB path.
- **Tenant boundary:** every request carries `X-User-Id` + `X-Project-Slug`; controllers resolve via
  `ProjectDirectory.ListForUserAsync` + membership check (401/403/404) — copy `DashboardController.Resolve`.
  Writes go through `TenantWriteService` (runs as `qs_app`, RLS-enforced) or SQL `SECURITY DEFINER`
  procedures; the DB enforces invariants and you surface its typed errors as 409.
- **Domain:** `CostCentrePeriod` (per centre-period: `AcCumulative`, `BacAed`, `ActualPctComplete`,
  `Cpi`, `PvAed`, `EvAed`, resource-split ACs; `Rolling3mCpi` is null on the Postgres path — derive it).
  Reusable analytics: `RollingOriginEvaluator` (expanding-prefix walk pattern), `FeatureBuilder` (adjacency
  idea; `Delta` is private — write your own helper), `Metrics`/`ForecastMetrics`.
- **DTOs:** positional `record`s in `Web.API/Contracts/*Dtos.cs`. Sanitize non-finite doubles → null
  (System.Text.Json rejects Infinity/NaN).
- **Frontend:** `src/api/client.ts` (`session {userId, projectSlug}` + `get/post/send` helpers that add the
  headers; add typed methods); a tab in `App.tsx` (the `Tab` union + `TABS` + render block); components under
  `src/components/`. Reuse the inline **`Spark` SVG** (EvmOverview.tsx) for charts and `ValidationPanel.tsx`
  as the metrics-panel template. Reuse CSS classes (`.card`, `.grid`, `.tag`, `.pill`, `.kpi`, `.spark`).
- **DB migrations** (only if the idea needs schema): raw SQL in `db/migrations/000N_*.sql`, wired into
  `db/apply.sh`; contract tests in `db/tests/*.sql` run by `db/run_tests.sh`. Respect the invariants
  (RLS FORCE, draft-only estimate edits `0008`, closed-period freeze `0005`, append-only ledger `0007`,
  immutable currency). Governed, not raw.

### Step 1 — Understand + explore (read-only)
Read `{{IDEA_FILE}}` fully (including any CEO/Codex review appendices — honor their reframes). Then launch
**1–3 `Explore` subagents in parallel** to inventory what to reuse: the relevant `Core` utilities and their
signatures, the exact `CostCentrePeriod`/DB-view fields available (and which are null on the Postgres path),
the registry/`ProjectSnapshot` shape, the controller/DTO/tenant-resolution pattern, and the React
chart/client patterns. Do not propose code yet — gather facts with file paths.

### Step 2 — Clarify scope (AskUserQuestion)
Ask 2–4 scoping questions whose answers change the build, e.g.: runtime placement (C# in Core vs sidecar),
model/approach complexity, UI scope (full vs core), and any governance choice. Recommend the option that
reuses the platform and keeps one stack. Wait for answers.

### Step 3 — Write the integration plan
Write `plan/<idea-slug>-integration-plan.md` with: **Context** (what/why, decisions confirmed), the concrete
module layout (files + key signatures), how it plugs into the snapshot/registry, API endpoints + DTOs,
frontend tab/components, a **Verification** section (unit tests + build + run + curl + browser), and an
**Out-of-scope/guardrails** section. Front-load any **leakage/validity guards** (features vs labels, no
target leakage, honest metrics). Keep it scannable.

### Step 4 — Codex-review loop until clean  (run via `/loop`)
Loop until codex reports no blocking findings:
1. Run codex to review the plan (read-only, high effort), e.g.:
   ```
   codex exec "<review the plan at plan/<slug>-integration-plan.md; list BLOCKING findings + fixes; terse>" \
     -C "$(git rev-parse --show-toplevel)" -s read-only -c 'model_reasoning_effort="high"' --json
   ```
2. Append its findings as a `## Codex Review — round N` section to the plan.
3. Spawn a **claude subagent** (general-purpose) to revise the plan body so every blocking finding is
   genuinely resolved (not just noted) and append a `### Codex round-N reconciliation` table.
4. Repeat. Stop when codex says "no remaining blocking findings," record the clean verdict, and **commit the
   plan** (`git add plan/<slug>-integration-plan.md && commit`).

### Step 5 — Implement (in order, building as you go)
Implement per the approved plan: **Core** module first → wire into `ProjectSnapshot`/`Build()` → **API**
controller + DTOs (reuse the tenant `Resolve`) → **frontend** client methods + tab + components → **unit
tests** in `tests/QsEarlyWarning.Tests` (xUnit). Build after each layer (`dotnet build QsEarlyWarning.sln`)
and fix errors immediately. If the idea needs schema, add the migration + `db/tests` and keep `run_tests.sh`
green.

### Step 6 — Verify end-to-end (honest)
- `dotnet build QsEarlyWarning.sln`; `cd frontend/qs-early-warning && npx tsc --noEmit`.
- `dotnet test tests/QsEarlyWarning.Tests/…` — all green (existing + new). Tests verify **calculations and
  constraints**, not a predetermined win.
- If schema changed: `db/run_tests.sh` still `PHASE-0 GATE: PASS` (+ new suites).
- Run the app: API on `:5070`, Vite on `:5173` (see commands below). `curl` the new endpoints with
  `-H "X-User-Id: 1" -H "X-Project-Slug: tower-x"`; confirm 200 for a member, **403 for a non-member**
  (`X-User-Id: 2`), and correct 400/404/409.
- Drive the new UI in a headless browser (Playwright MCP): navigate `http://localhost:5173`, click the new
  tab, screenshot, and confirm only benign console noise (favicon 404). Fix any real bug found and re-verify.
- **Report the real results.** If a model/feature doesn't beat its baselines or coverage is below nominal,
  say so plainly — the deliverable is the honest measured artifact.

### Step 7 — Ship
Stage only `QsEarlyWarning/` (never the root artifacts: `.DS_Store`, `.playwright-mcp/`, screenshots,
`.codeboarding/`). Commit with a descriptive message + the co-author trailer, and push `main` only when asked.

### Run/verify commands (reference)
```bash
# reset + reimport data (only if you changed migrations)
psql -d postgres -c 'DROP DATABASE IF EXISTS qs_phase1;' -c 'CREATE DATABASE qs_phase1;'
QsEarlyWarning/db/apply.sh qs_phase1
dotnet run --project QsEarlyWarning/tools/QsEarlyWarning.Importer -c Release

# run API + dashboard (background)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project QsEarlyWarning/src/QsEarlyWarning.Web.API \
  -c Release --no-launch-profile --urls http://localhost:5070
cd QsEarlyWarning/frontend/qs-early-warning && API_URL=http://localhost:5070 npm run dev   # → :5173
```

### Principles (from the Idea-2 run)
- **Reuse > rebuild** — snapshot, tenant resolution, DTO style, Spark SVG, metrics.
- **Governed, not raw** — respect the DB invariants; surface its rejections.
- **Honest verification** — measured, not asserted; a "no win" result is a valid, reportable outcome.
- **Leakage first** — define availability on the label/target, keep features causal, exclude columns that
  leak the baseline.
- **Codex to clean, then implement** — don't start coding until the plan clears the review loop.
