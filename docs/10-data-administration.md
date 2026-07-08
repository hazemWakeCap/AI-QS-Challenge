# Feature 10 — Data Administration

## What it is

Governed generic CRUD over the project's underlying tables. The UI auto-generates grids and
forms from a server-provided entity registry, so an admin can inspect and correct the raw data
without a bespoke screen per table.

## Who it's for

The QS-admin or data owner who needs to view or fix the underlying records (cost centres,
periods, estimate lines, etc.) directly.

## How it works

- The **entity registry** describes each table — its key, display name, column metadata, and
  capabilities — so the front end can build grids and forms automatically. This registry is
  static metadata and needs no tenant scoping.
- CRUD operations (list / read one / create / update / delete) are dispatched to a
  `GenericCrudService`, scoped to the resolved tenant.
- **The database enforces every invariant.** The controller resolves the tenant, dispatches the
  operation, and maps any rejection to the right HTTP status — it doesn't re-implement business
  rules.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/entities` | The entity registry (keys, display names, columns, capabilities). |
| `GET /api/v1/entities/{key}` | List rows of one entity (accepts `?column=value` equality filters for registered columns). |
| `GET /api/v1/entities/{key}/{id}` | Read a single row. |
| `POST /api/v1/entities/{key}` | Create a row. |
| `PUT /api/v1/entities/{key}/{id}` | Update a row. |
| `DELETE /api/v1/entities/{key}/{id}` | Delete a row. |

## UI

The **Data Admin** tab (`DataAdmin`) — the one tab that's usable even before a project has data
loaded, so you can bootstrap an empty project.

## Guarantees & limits

- **Database-enforced integrity** — invariants live in the DB, not the controller, so CRUD can't
  bypass the rules.
- **Tenant-scoped writes** — every mutating operation is scoped to the resolved tenant.
- **Metadata-driven UI** — grids/forms are generated from the registry. A new column must first
  be added to the static `EntityRegistry`; once registered, the generic UI renders it without any
  per-component changes.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _"New columns surface without front-end changes" is overstated_ — a column must first be added
  to the static `EntityRegistry`. **Fixed:** qualified the claim.
- _List endpoints accept equality filters_ — **Fixed:** documented `?column=value` on the list
  endpoint.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
