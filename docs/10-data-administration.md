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

### Workbook sheet nav

The 14 normalized tables are a relational schema, not the 5 spreadsheet sheets a QS opened
(`1_BOQ`, `2_ESTIMATE_NORMS`, `3_BOQ_MAPPING`, `4_ESTIMATE_DATASHEET`, `9_HISTORICAL_DATA`). So the
UI groups them into **7 workbook "sheets"** and navigates sheet-first: a row of sheet-tabs (ordered
by `groupOrder`, System & Import last and dimmed), then table chips within a sheet that has more than
one table, then a per-table lineage header (sheet-code breadcrumb + plain-language blurb + a
read-only pill for import/procedure-managed tables).

The grouping is **server-driven**: `EntityDescriptor` carries `Group`/`GroupLabel`/`GroupOrder`
(shared within a group, drive the nav) and `SheetRef`/`Blurb` (**per-entity** lineage). Nav order is
`GroupOrder` then `Order` — never the registry array position (which starts with `estimate-versions`).

**Provenance is per-entity and must stay accurate.** `WorkbookImporter` imports the sheet-9 *source
inputs* — cost centres, BAC baselines, the planned S-curve, reporting periods, and per-period actual
facts — directly from `9_HISTORICAL_DATA` into `cost_centres`/`cost_centre_baselines`/
`cost_centre_plan_periods`/`reporting_periods`/`cost_centre_periods`, so those carry
`SheetRef = "9_HISTORICAL_DATA"`. What is *derived* (not imported) is the **computed EVM**
(CPI/SPI/EAC/VAC/alerts) — the output sheets `CLAUDE.md` intentionally excludes. Because lineage is
per-entity, a single group can mix imported and non-imported tables: `cost-deltas` sits in the
**Periods & Actuals** group but is captured live via the cost-ledger flow (`SheetRef = null`), so it
never shows the `9_HISTORICAL_DATA` breadcrumb its group-mates carry. `EntityRegistryShapeTests`
guards all of this.

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
