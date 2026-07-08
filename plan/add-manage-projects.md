# Plan: Add & manage projects from the dashboard

> On approval, this plan is also to be saved to `/Users/hazem/hackathon/AI-QS-Challenge/plan/` (the user's requested location).

## Context

The QS Early-Warning app (`QsEarlyWarning/`, .NET 8 API + React/Vite frontend + Postgres w/ RLS) is **already multi-tenant end to end**: the DB is keyed by `project_id`, every read path resolves a project via the `X-Project-Slug` header + `ProjectSnapshotRegistry`, and the frontend already has a project-switcher dropdown. The one thing missing is a way to **create and manage projects from the UI** — today a project can only be added by running the CLI importer (`tools/QsEarlyWarning.Importer`), which hardcodes name (`"Tower X (slug)"`), currency (`AED`) and owner (`user_id = 1`).

This change adds an in-app **Projects admin**: create an empty project OR create one by uploading a workbook, plus rename, delete, and re-import/refresh. It reuses the existing `WorkbookImporter` pipeline (which already creates the `qs.projects` row + owner membership + ingests all sheet-9 data in one transaction) and the existing tenancy/authorization patterns.

**Out of scope** (note as known limitations): member management; generalizing the Excel-based estimate/stress-test (`EstimateWorkbookLoader`) beyond `tower-x`; real auth (still `X-User-Id`/`session.userId = 1`).

---

## Backend (.NET — `QsEarlyWarning/src/`)

Web.API already references Infrastructure, so no new project references — just `using QsEarlyWarning.Infrastructure.Import;`. The CLI `tools/Importer` project is untouched.

### 1. Parameterize project metadata in the importer
`Infrastructure/Import/WorkbookImporter.cs` — the two INSERTs at `:39-45` hardcode name/currency/owner. Add a small `record ProjectMeta(string Name, string Currency, long OwnerUserId)` and a new `Import(...)` overload taking it; keep the old 4-arg signature delegating with the current defaults so the CLI (`tools/.../Program.cs:31`) keeps working. Use `@n`/`@cur`/`@owner` params in the `qs.projects` insert and the `qs.project_memberships` insert.
- **Idempotency stays single-path:** `Purge(...)` (`:37`, SQL at `:306-326`) still fully deletes the slug's rows + project. The *caller* decides the meta: for re-import/populate-existing it passes the project's **current** meta (read first); for create-new it passes user-supplied meta. One code path, no special-casing.

### 2. New `ProjectAdminService` — `Infrastructure/Postgres/ProjectAdminService.cs`
Mirror `WorkbookImporter`'s approach (raw connection string, `SET ROLE qs_bypass`, one transaction — consistent with how project creation already works). Methods:
- `Task<long> CreateEmptyAsync(string slug, ProjectMeta meta, CancellationToken)` — `INSERT INTO qs.projects` + owner `project_memberships` row (no data). Throws on slug collision.
- `Task<ProjectMeta?> GetMetaAsync(string slug, CancellationToken)` — reads current name/currency/owner (used by re-import to preserve meta).
- `Task UpdateMetaAsync(string slug, string? name, string? currency, CancellationToken)` — `UPDATE qs.projects`.
- `Task DeleteAsync(string slug, CancellationToken)` — reuse the exact child→parent delete order from `WorkbookImporter.Purge` (`:306-326`).

### 3. New `ProjectImportService` — `Infrastructure/Import/ProjectImportService.cs`
Thin wrapper capturing `connString` + `IWorkbookImporter`, so controllers don't need the (DI-private) connection string. `Task<ReconciliationReport> ImportAsync(string workbookPath, string slug, string actor, ProjectMeta meta, CancellationToken)` → `Task.Run(() => _importer.Import(...))` (importer is synchronous/blocking).

### 4. DI registration — `Web.API/Program.cs` (composition root ~`:10-63`)
`connString` (`:29`) is currently captured only in factory lambdas. Add, near the other singletons:
```
builder.Services.AddSingleton<IWorkbookImporter>(sp => new WorkbookImporter(sp.GetRequiredService<IPanelLoader>()));
builder.Services.AddSingleton(new ProjectAdminService(connString));
builder.Services.AddSingleton(sp => new ProjectImportService(connString, sp.GetRequiredService<IWorkbookImporter>()));
```
(`IPanelLoader`/`ExcelPanelLoader` already registered at `:16`.)

### 5. Endpoints — extend `Web.API/Controllers/ProjectsController.cs`
Keep the existing `GET`. Add (inject `ProjectAdminService`, `ProjectImportService`, `ProjectDirectory`, `ProjectResolver`, `IProjectSnapshotRegistry`, `TenantContext`):

| Verb / route | Body | Auth | Action |
|---|---|---|---|
| `POST /api/v1/projects` | `{name, slug, currency}` | `X-User-Id` only | `CreateEmptyAsync`, owner = UserId → `ProjectDto` |
| `POST /api/v1/projects/import` | multipart `file,name,slug,currency` | `X-User-Id` only | reject if slug exists → save `IFormFile` to temp → `ProjectImportService.ImportAsync(meta{owner=UserId})` → `RebuildAsync` → report summary |
| `POST /api/v1/projects/{slug}/import` | multipart `file` | membership | `GetMetaAsync` (preserve) → import → `RebuildAsync(projectId, userId)` |
| `PATCH /api/v1/projects/{slug}` | `{name?, currency?}` | membership | `UpdateMetaAsync` |
| `DELETE /api/v1/projects/{slug}` | — | membership (owner) | `DeleteAsync` + `ProjectResolver.Invalidate(slug)` |

- **Authorization for existing-project ops:** reuse the membership check via `ProjectDirectory.ListForUserAsync(userId)` + `.FirstOrDefault(p => p.Slug == …)` — the exact pattern in `WorkflowController.ResolveProject`. Create/import-create need only `X-User-Id` (same as `GET`).
- **Post-import refresh:** call `_registry.RebuildAsync(projectId, userId)` after a successful import so new/refreshed data appears in reads — mirrors `WorkflowController.Write`.
- **Temp files:** persist `IFormFile` to `Path.ChangeExtension(Path.GetTempFileName(), ".xlsx")`, `try/finally` delete. Add a `[RequestSizeLimit]` on the import actions sized for the workbook (~a few MB); no multipart config exists today.
- **Error mapping:** `DataContractException` (bad workbook) → `400`; slug collision → `409`; a failed `ReconciliationReport` (`Passed == false`) → return the report with `PublishViolations`/`FailureReason` so the UI can show why (the importer already rolls back internally on validation failure).
- **`ProjectResolver.Invalidate(string slug)`** — add this method (`Tenancy/ProjectResolver.cs`, remove the slug from its `ConcurrentDictionary` cache) and call it on delete/rename so the cached slug→id can't go stale.

---

## Frontend (`QsEarlyWarning/frontend/qs-early-warning/src/`)

Plain React 18 + TS + Vite, no UI lib. Reuse global classes from `styles.css` (`.card`, `.card.narrow`, `.capture`, `.panel-head`, `.pill`, `.grid`/`.grid-scroll`, `.btn-sm`, `.error`, `.ok-msg`, `.tag`). Model the new component on `DataAdmin.tsx` (list + inline `.card` "modal" form) and `CapturePanel.tsx` (controlled form w/ `busy`/`msg`).

### 1. `api/client.ts`
- Add a **multipart** helper (headers = `X-User-Id` + `X-Project-Slug`, **no** `Content-Type` — the browser sets the multipart boundary): `postForm<T>(url, form: FormData)`.
- Add methods: `createProject({name,slug,currency})` → `post`; `importProject(FormData)` / `reimportProject(slug, File)` → `postForm`; `updateProject(slug, body)` → `send("PATCH", …)`; `deleteProject(slug)` → `send("DELETE", …)`.
- For create/import-create, clear `session.projectSlug` on the call path (or let `headers()` omit it when empty — it already does) so no stale project header is sent before one is selected.

### 2. New component `components/ProjectsAdmin.tsx`
- Lists projects (`api.projects()`), one row per project with **Rename**, **Re-import** (`<input type="file" accept=".xlsx">`), **Delete** (native `confirm`) actions.
- **"+ New Project"** inline form with two modes: *Empty* (name/slug/currency) and *From workbook* (name/slug/currency + file). Show the returned `ReconciliationReport` summary (`CostCentres/Periods/Facts`, or `PublishViolations` on failure) in an `.ok-msg`/`.error` box.
- Props `{ onProjectsChanged: () => void }` so App re-loads the switcher after create/delete/rename.

### 3. `App.tsx` wiring
- Extract the project-loading logic from the mount effect (`:42-48`) into a reusable `loadProjects()` and pass it down; on delete-of-current-slug, re-point `session.projectSlug`/`setSlug` to the first remaining project (or empty).
- Add a **"Projects"** tab: extend the `Tab` union + `TABS` (`:16-27`) and render `{tab === "projects" && <section className="card"><ProjectsAdmin onProjectsChanged={loadProjects} /></section>}`. Optionally a **"+ New Project"** shortcut button beside the switcher `<select>` (`:75-87`) that jumps to the tab.

---

## Risks / things to verify during build
- **Empty project rendering:** a create-empty project has no estimate version and no cost-centre rows. Confirm `ProjectSnapshotRegistry.GetOrBuildAsync` + the read endpoints (overview/watchlist/costCentres) return graceful **empty states**, not `500s`, when selected. Add empty-state guards if needed.
- **Bypass role reach:** `ProjectAdminService`/importer run as `qs_bypass`; confirm the API's login can `SET ROLE qs_bypass` (the importer already relies on this locally).
- **Re-import is destructive** (`Purge` deletes the slug's data first) — the UI `confirm` copy must say "this replaces existing data".

---

## Verification (end to end)
1. Start the stack (use the **`/run_system`** skill, or `dotnet run` in `Web.API` + `npm run dev` in the frontend).
2. **Create empty:** Projects tab → New Project (Empty) → appears in switcher; selecting it shows empty states, no errors.
3. **Upload-to-create:** New Project (From workbook) with a copy of `data/Tower_X_Project_Data.xlsx` under a new slug (e.g. `tower-y`) → reconciliation summary shows non-zero CostCentres/Periods/Facts → switch to it → overview/watchlist populate.
4. **Re-import:** re-upload to an existing project → data refreshes, still consistent.
5. **Rename / currency:** PATCH reflected in the switcher label `name (currency)`.
6. **Delete:** removes it; if it was selected, switcher falls back to another project; DB shows the `qs.projects` row and children gone.
7. **DB spot-check:** `SELECT slug,name,reporting_currency FROM qs.projects;` and membership/`import_runs` rows for the new slug.
8. **Regression:** the CLI importer (`dotnet run --project tools/QsEarlyWarning.Importer -- …`) still works via the retained 4-arg overload.
