# Feature 11 — Multi-Tenant Security

## What it is

The cross-cutting mechanism that keeps every project's data isolated per user. It's not a screen
— it's the guarantee underneath every **project-data** feature: a caller only ever sees or
changes data for a project they're a member of, enforced by the database itself.

A few endpoints are deliberately **global / non-tenant** and sit outside this mechanism:
`/api/v1/health`, `/api/v1/validation-summary` (both report the process's startup Tower X
workbook model), and the static `/api/v1/entities` registry metadata.

## Who it's for

Everyone, implicitly. It's the trust boundary that lets multiple QSs and projects share one
deployment safely.

## How it works

- **Identity + selected tenant come from request headers**: `X-User-Id` is the authenticated
  principal and `X-Project-Slug` is the selected project. (In this build headers stand in for a
  real auth layer; both are then validated against the database.)
- `TenantContextMiddleware` reads those headers into a per-request tenant context.
- `ProjectResolver` maps the slug → project id (cached). This runs as a **bypass role** because
  slug→id isn't tenant-sensitive — and RLS still gates every actual data read downstream.
- **PostgreSQL Row-Level Security (RLS)** is the real enforcement: the user id is passed into the
  database session, and RLS policies restrict every read/write to the caller's memberships. A
  non-member simply gets no rows.
- Reads resolve authorization **before** doing work — e.g. the copilot builds its tools only
  after the RLS-scoped snapshot is resolved, so a non-member never reaches a tool call.

## Status codes

| Code | Meaning |
|------|---------|
| `401` | No usable identity context — `X-User-Id` missing / non-numeric / non-positive. On **selected-project** endpoints, a missing `X-Project-Slug` also fails here. (Project **listing / creation / new-project import** need only `X-User-Id`; the **global** endpoints need neither header. A syntactically valid but non-existent user id is **not** rejected here — it proceeds to membership resolution and typically yields `403`.) |
| `403` | Not a member of the requested project. Because several controllers check the caller's membership list, this is also returned for an **unknown** project — i.e. `403` covers both "unknown" and "unauthorized." |
| `404` | Endpoint-dependent: some controllers return `404` for an unknown project or a valid-but-unserved resource (e.g. no watchlist artifact for a period). |

> Exact behaviour is **endpoint-dependent**: `403` vs `404` for an unknown project varies by
> controller, so don't rely on one specific code to distinguish "unknown" from "unauthorized."

## Guarantees & limits

- **Database-enforced isolation for tenant tables** — for RLS-governed project-data reads and
  writes, RLS (not application code) is the last line of defence, so a controller bug can't leak
  another tenant's rows. This guarantee does **not** extend to the bypass-role project-admin path
  (resolver, re-import/patch/delete) or the global endpoints listed above.
- **Cache invalidation on change** — the resolver is invalidated on re-import (a new project
  row/id), delete, and metadata patch (name/currency), so the next resolve reflects the new
  state. (The slug itself is immutable — there is no rename-the-slug operation.)
- **Header-based identity is a stand-in** — this build trusts `X-User-Id`/`X-Project-Slug` in
  place of a full auth provider; a production deployment would issue those from real
  authentication.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _"Protects every other feature" is false for global endpoints_ — **Fixed:** listed
  `/health`, `/validation-summary`, and static `/entities` as global/non-tenant.
- _Unknown projects don't consistently return `404` (often `403`)_ — **Fixed:** the status table
  now describes endpoint-dependent `403`/`404` behaviour.
- _A valid-but-nonexistent `X-User-Id` isn't a `401` — it becomes `403`_ — **Fixed:** narrowed
  `401` to missing/non-numeric/non-positive id or missing project header.
- _"Invalidation on rename" is wrong — slugs can't be renamed_ — **Fixed:** now says invalidation
  happens on re-import/delete/metadata patch, and the slug is immutable.
- _"A bug in a controller can't leak" is too absolute_ — **Fixed:** narrowed the guarantee to
  RLS-governed tenant-table reads/writes, excluding the bypass-role admin path and global
  endpoints.

**Round 2 (CHANGES REQUESTED) — resolved.**

- _The `401` row implied every request needs `X-Project-Slug`, but listing/creation/new-project
  import need only `X-User-Id` and global endpoints need neither_ — **Fixed:** scoped the missing
  project-header cause to selected-project endpoints and noted the header-free cases.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
