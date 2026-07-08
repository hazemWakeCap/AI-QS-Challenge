# Current Implementation Review — Project Management / Import UX

Date: 2026-07-08

## Scope reviewed

Reviewed the current working tree changes around:

- In-app project create/import/re-import/update/delete API.
- Workbook importer metadata support.
- Frontend Projects tab and dashboard empty-state/currency formatting updates.
- Build/test status.

## Validation run

- `pnpm build` in `QsEarlyWarning/frontend/qs-early-warning` ✅ passed.
- `dotnet test QsEarlyWarning/QsEarlyWarning.sln` ⚠️ API/unit tests passed, but DB gate failed because local PostgreSQL connects as role `root`, which does not exist. This appears environment-related rather than a compile failure.

## Findings for Claude to handle

### 1. Re-import drops every non-owner project membership

**Severity:** High / P1

**Files:**

- `QsEarlyWarning/src/QsEarlyWarning.Web.API/Controllers/ProjectsController.cs:100-103`
- `QsEarlyWarning/src/QsEarlyWarning.Infrastructure/Import/WorkbookImporter.cs:326-328`

`Reimport` preserves only `ProjectMeta` containing name, currency, and a single owner user id, then calls the importer. The importer `Purge` deletes `project_memberships` and recreates only one owner membership. For any project with editors/viewers/service users, a normal re-import silently removes all of those memberships; if an editor triggers the re-import, the editor can lose access immediately after the operation.

**Suggested direction:** Before re-import, snapshot all memberships for the existing project and restore them after the new project row is inserted, or change the importer flow so project identity/memberships are not deleted during in-app re-import.

### 2. Working tree contains generated/local artifacts that should likely not be committed

**Severity:** Medium / P2

Examples currently modified/untracked:

- `.codeboarding/logs/wrapper-server.log`
- `.DS_Store`
- `.claude/`
- `.wstack/`
- root screenshot PNG duplicates such as `qs-app-forecast.png`, `qs-dashboard-capture.png`, etc.

These are unrelated to the implementation and can make the patch noisy or leak local runtime state.

**Suggested direction:** Revert/remove local artifacts and add ignore rules if needed before finalizing the change.

## Resolution (handled 2026-07-08)

Both findings addressed and re-reviewed by Codex until convergence (**NO MATERIAL FINDINGS**).

- **Finding 1 (P1) — RESOLVED.** `ProjectAdminService` gained `GetMembershipsAsync` + `RestoreMembershipsAsync` (run as `qs_bypass`, `INSERT … ON CONFLICT ON CONSTRAINT uq_membership DO NOTHING`). `ProjectsController.Reimport` now snapshots all memberships before the import and restores them after, so editors/viewers/service users survive a re-import; the importer-recreated owner is skipped via the conflict clause, and a failed import (rolled back) makes the restore a safe no-op. Verified functionally: an editor(2) + viewer(3) added to `tower-x` both survive a re-import (previously only the owner remained).
- **Finding 2 (P2) — RESOLVED.** Added a root `.gitignore` (`.DS_Store`, `.claude/`, `.wstack/`, `.codeboarding/logs/`, `/qs-*.png`, `bin/`, `obj/`, `node_modules/`, `dist/`); reverted the local mutation to the tracked `.codeboarding/logs/wrapper-server.log` and then `git rm --cached` it so it is no longer tracked (file kept on disk). `git ls-files .codeboarding/logs/` is now empty; the working-tree diff shows only real source changes.

No new correctness issues were introduced by the fixes.

## Notes / non-blocking observations

- Frontend TypeScript/Vite build is clean.
- The API implementation catches workbook contract and Postgres import errors and returns user-visible messages, which is good for the new upload flow.
- The empty-project guard in `App.tsx` avoids calling EVM endpoints when `activeEstimateVersionId` is null, which matches current backend behavior.
- There is no client-side workbook extension/content validation beyond `accept=".xlsx"`; backend importer failures are still surfaced as bad requests.
