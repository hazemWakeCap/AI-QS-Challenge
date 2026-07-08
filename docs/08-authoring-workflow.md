# Feature 8 — Authoring Workflow

## What it is

The authorized write-side of the app: the controlled actions that move a project forward in
time — open and close reporting periods, capture monthly progress and cost, publish an estimate
version, rebaseline, and cut over. Every write is scoped to the caller's selected project and
refreshes the snapshot so the read side reflects the change immediately.

## Who it's for

The QS or project controls owner responsible for month-end close and keeping the project's
record up to date.

## How it works

- Each write is authorized by the same tenant sequence as reads (RLS + a procedure-level
  membership check), so you can only author on projects you belong to.
- After a successful write, the project **snapshot is refreshed**, so the dashboard, watchlist,
  forecast, and copilot all immediately see the new state.
- The main actions:
  - **Periods** — list, `open`, `close` a reporting period.
  - **Capture** — record monthly `progress` and `cost` for the open period.
  - **Estimate** — `publish` an estimate version.
  - **Rebaseline** — reset the baseline at a period.
  - **Cutover** — the cut-over action for transitioning the project's state.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/periods` | List reporting periods. |
| `POST /api/v1/periods/{ordinal}/open` | Open a period. |
| `POST /api/v1/periods/{ordinal}/close` | Close a period. |
| `POST /api/v1/capture/progress` | Capture monthly progress. |
| `POST /api/v1/capture/cost` | Capture monthly cost. |
| `POST /api/v1/estimate-versions/{versionId}/publish` | Publish an estimate version. |
| `POST /api/v1/periods/{ordinal}/rebaseline` | Rebaseline at a period. |
| `POST /api/v1/cutover` | Cut over the project state. |

## UI

The **Periods & Estimate** tab (`PeriodsPanel`) and the **Monthly Capture** tab
(`CapturePanel`). Note the UI does **not** surface every action: `CapturePanel` captures
**progress only**, and **cost capture, rebaseline, and cutover are API-only** (endpoints exist,
no UI controls yet).

## Guarantees & limits

- **Authorized writes only** — the database enforces membership and invariants; a rejected write
  is mapped to **`409 Conflict`** with the DB message.
- **Consistent reads after writes** — the snapshot refresh keeps every read feature in sync.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _Named UI components don't expose all actions_ — `CapturePanel` is progress-only; cost capture,
  rebaseline, cutover are API-only. **Fixed:** stated which actions are API-only.
- _Rejected writes map specifically to `409 Conflict`_ — **Fixed:** named `409` instead of a
  vague status range.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.
