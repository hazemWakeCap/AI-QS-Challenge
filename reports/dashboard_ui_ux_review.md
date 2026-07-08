# Dashboard UI/UX Review

Date: 2026-07-08

## Scope reviewed

Reviewed the current dashboard frontend in `QsEarlyWarning/frontend/qs-early-warning`, with emphasis on:

- Dashboard shell, project/period controls, and 10-tab navigation.
- EVM overview, cost-centre table, watchlist, forecast, stress test, Copilot, Data Admin, and Projects flows.
- Loading, empty, success, and error states.
- Current CSS token system, responsive rules, and existing screenshot artifacts in the repository.

## Validation run

- `pnpm build` in `QsEarlyWarning/frontend/qs-early-warning` passed.
- Existing screenshots show both the current "QS Cost — System of Record" UI and older "QS Cost Early-Warning" captures. Treat screenshots as partial evidence only; a fresh browser pass should be taken before closing visual QA.

## Findings for Claude to handle

### 1. Mobile navigation will become difficult to scan and use

**Severity:** High / P1

**Files:**

- `QsEarlyWarning/frontend/qs-early-warning/src/App.tsx:18-30`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:166-180`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:262-268`

The dashboard now exposes 10 top-level tabs. On narrow screens the tabs only wrap and shrink slightly; there is no horizontal tab scroller, grouped navigation, sticky context, or active-section affordance beyond a thin underline. This is workable on desktop but will be noisy and easy to miss on mobile, especially with long labels like "Periods & Estimate" and "Model & Copilot".

**Suggested direction:** Convert the mobile tab bar to a horizontally scrollable tablist with stable active positioning, or group operational tabs under clearer sections such as Overview, Data Entry, Forecasting, Admin. Keep project and period controls sticky or visually tied to the selected tab so users do not lose context while scrolling dense tables.

### 2. Card-inside-card form panels make admin screens visually heavy

**Severity:** Medium / P2

**Files:**

- `QsEarlyWarning/frontend/qs-early-warning/src/App.tsx:119-133`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/ProjectsAdmin.tsx:88-118`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/DataAdmin.tsx:111-140`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:173-178`

The main content area already wraps Projects and Data Admin in `.card`, then inline create/edit forms render another `.card narrow` inside that surface. This creates nested elevation, repeated borders, and a modal-like visual hierarchy without modal behavior. The result is especially busy in admin workflows where users need to compare the form with the table below.

**Suggested direction:** Use an unframed inline form section or a true modal/drawer. If it stays inline, style it as `.form-panel` with a flat background, no outer shadow, and a clear heading/action row rather than another full card.

### 3. Table-heavy views rely on overflow but lack mobile-friendly structure

**Severity:** Medium / P2

**Files:**

- `QsEarlyWarning/frontend/qs-early-warning/src/components/CostCentreGrid.tsx:27-52`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/DataAdmin.tsx:143-167`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/ProjectsAdmin.tsx:121-166`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:90-95`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:199-201`

Cost Centres, Data Admin, and Projects are wide tables inside `.grid-scroll`. Horizontal overflow preserves data, but on mobile it forces users to pan across critical columns and row actions. The current CSS does not pin identity/action columns, provide row summaries, or switch to compact cards for high-value workflows.

**Suggested direction:** For desktop, keep the dense grid but make key columns sticky where useful. For mobile, either add row summary cards for Projects/Data Admin or provide a column-prioritized compact table that keeps identity, status, and primary action visible without horizontal panning.

### 4. Destructive and long-running actions need stronger interaction feedback

**Severity:** Medium / P2

**Files:**

- `QsEarlyWarning/frontend/qs-early-warning/src/components/ProjectsAdmin.tsx:68-75`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/ProjectsAdmin.tsx:150-157`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/DataAdmin.tsx:83-87`
- `QsEarlyWarning/frontend/qs-early-warning/src/components/DataAdmin.tsx:158-160`

Re-import and delete use native `confirm()` dialogs and inline buttons. The copy warns about replacement/deletion, but the UI does not provide a structured review step, visible progress state beyond disabled controls, or post-action recovery guidance. Re-import is especially high-impact because it replaces project data and currently sits beside routine actions in the Projects table.

**Suggested direction:** Promote re-import/delete into explicit confirmation dialogs with project name, slug, selected filename, and consequences. During import, show a persistent busy state with filename and action type. After completion, keep a reconciliation summary visible and make the next action obvious.

### 5. Button styling migration is inconsistent and may regress future controls

**Severity:** Low / P3

**Files:**

- `QsEarlyWarning/frontend/qs-early-warning/src/components/DataAdmin.tsx:104`
- `QsEarlyWarning/frontend/qs-early-warning/src/styles.css:139-163`

Most controls now use the `.btn` plus variant pattern, but Data Admin still renders the Add action with only `className="btn-sm"`. CSS keeps `.btn-sm` self-sufficient through a legacy compatibility rule, which avoids breakage today but makes the button API ambiguous for future contributors.

**Suggested direction:** Finish migrating button markup so every command button uses `.btn` plus a variant and optional size. Then simplify the legacy compatibility rule to known legacy containers only, or remove it once all callers are migrated.

## Positive notes

- The current UI has a clear dark token system, self-hosted fonts, visible focus states, and reduced-motion handling.
- Shared `Spinner` and `EmptyState` components are a good improvement over bare loading text.
- The project empty-state guard avoids noisy API failures for projects without imported data.
- Numeric tables use tabular formatting and right alignment, which suits QS/EVM workflows.

## Follow-up validation recommended

- Start the API and Vite app, then capture fresh screenshots for all 10 tabs at desktop and mobile widths.
- Keyboard-test the tab bar, project switcher, file upload labels, inline edit rows, Copilot composer, and table actions.
- Check mobile behavior for Cost Centres, Projects, and Data Admin specifically; these are the highest-risk responsive surfaces.
