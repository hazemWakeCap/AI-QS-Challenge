# Feature 9 — Project Management

## What it is

The multi-project front door: create an empty project, import one from a workbook, switch
between the projects you belong to, refresh a project's data, rename it, or delete it. This is
what makes the app multi-tenant rather than a single-project tool.

## Who it's for

Any user who is a **member** of a project — membership (not a distinct owner/admin role) is what
authorizes management actions here.

## How it works

- **List** returns only the projects the authenticated caller is a member of — it's the tenant
  switcher, and it needs only the caller's identity (no project selected yet).
- **Create empty** makes a new project with no data, owned by the caller.
- **Import** creates a project by uploading a workbook (multipart): project row + owner + full
  data ingest, returning a reconciliation summary. `422` if the ingest doesn't tie out /
  activate.
- **Re-import** refreshes an existing project's data from a new workbook. This is
  **destructive** — it replaces the project's data — but metadata (name, currency) **and all
  existing memberships** are preserved and restored.
- **Patch** renames a project or changes its reporting currency (the slug itself is immutable).
- **Delete** removes a project and all of its data.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/projects` | Projects the caller is a member of (tenant switcher). |
| `POST /api/v1/projects` | Create an empty project. |
| `POST /api/v1/projects/import` | Create a project from an uploaded workbook. |
| `POST /api/v1/projects/{slug}/import` | Re-import (destructive refresh) an existing project. |
| `PATCH /api/v1/projects/{slug}` | Rename / change reporting currency. |
| `DELETE /api/v1/projects/{slug}` | Delete a project and all its data. |

## UI

The **Projects** tab (`ProjectsAdmin`), plus the "+ New / Manage" button in the header.

## Guarantees & limits

- **Membership-scoped** — you only see and manage projects you're a member of. Note there is
  **no separate owner/admin role check**: any member returned by the membership list can
  re-import, patch, or delete the project.
- **Import is validated** — the ingest returns a reconciliation summary and refuses to activate
  a project whose data doesn't tie out (`422`).
- **Re-import is destructive** — it replaces project data (metadata **and memberships**
  preserved); intended for a deliberate refresh, not an incremental edit.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- **BLOCKER** _Update verb is `PATCH`, not `PUT`_ — **Fixed:** corrected the API table to
  `PATCH /api/v1/projects/{slug}`.
- _Not owner/admin-scoped; any member can re-import/patch/delete_ — **Fixed:** reframed as
  membership-scoped with an explicit "no separate owner/admin role check" note.
- _Re-import preserves all memberships, not just owner metadata_ — **Fixed:** updated the
  preservation wording (name/currency + all memberships).

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
