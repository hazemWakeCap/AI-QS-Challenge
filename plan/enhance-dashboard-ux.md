# Plan: Enhance the dashboard UX (refined-dark focused polish)

> On approval, this plan is also to be saved to `/Users/hazem/hackathon/AI-QS-Challenge/plan/` (user's requested location).
> **Execution order the user asked for:** (1) save plan to that path → (2) `/loop` a Codex agent to review this plan and populate the **## Codex Review** section → (3) address findings in-plan → (4) ask Codex for a re-review → (5) only then implement the UI changes below.

## Context

The QS Early-Warning dashboard (`QsEarlyWarning/frontend/qs-early-warning`, plain React 18 + Vite, one 179-line `src/styles.css`, no UI lib) works but looks like a flat default "dark-admin" template. Root causes found in exploration:
- **Color-only tokens** (`styles.css:1-13`) — no spacing / radius / shadow / type scale, so every literal is hardcoded and inconsistent (padding 16 vs 18, radii 5–14 arbitrary).
- **Zero elevation** — no `box-shadow` anywhere; surfaces separate only by a faint border + ~8% lightness step, so hierarchy is weak.
- **System font, dense 11–13px type** with fractional sizes (11.5/12.5/10.5); no display font; tiny + fatiguing.
- **Dead/duplicate CSS**: `.kpi`/`.kpis` defined twice (`:67-71` vs `:117-123`), empty `.content`/`.composer button` rules; `.tag-amber` wired to `--danger` red while `--hist` is amber (muddled semantics).
- **No shared number formatting** — `money`/`ratio`/`pct` re-defined inline in 6+ components, inconsistently (some `" AED"` suffix, some symbol-less, DataAdmin raw).
- **Every button identical blue** (`.btn-sm`, `:141`); destructive actions use inline `style={{background:var(--danger)}}`.
- **Weak empty/loading states** (bare muted "Loading…" text), no `:focus-visible`, no transitions.

Goal: make it read as **intentional, modern, refined-dark** without changing structure, the 10-tab layout, or the hand-rolled SVG charts. Same app, polished.

**Scope decisions (confirmed):** Focused polish · Refined dark (dark-only, no light theme) · Keep top tabs, restyled (no sidebar).
**Out of scope:** sidebar nav, light theme, chart rewrites/tooltip engine, new pages, backend.

---

## The work

### 1. Design-token layer — `src/styles.css` `:root` (the foundation, do first)
Extend the existing color-only tokens with the missing scales, then refactor literals to reference them:
- **Spacing**: `--sp-1..--sp-8` (4,8,12,16,20,24,32,48). Replace ad-hoc paddings/margins/gaps.
- **Radius**: `--r-sm/-md/-lg/-pill` (6,10,14,999). Replace 5/8/9/11/14 literals.
- **Elevation**: `--sh-1/-2/-3` (subtle → card → overlay) plus a hairline top-highlight (`inset 0 1px 0 rgba(255,255,255,.04)`). This is the single biggest visual lift.
- **Type scale**: `--fs-eyebrow:11px / --fs-sm:12.5px / --fs-base:14px / --fs-h3:13px / --fs-h2:16px / --fs-h1:24px`, `--lh-*`. (h3/eyebrow get `text-transform:uppercase` + letter-spacing on the *class*, not the token — token values are valid CSS lengths only.) Kill fractional one-offs.
- **Refined palette**: deepen the bg ramp for real contrast steps (`--bg #0c1018`, `--surface #141b2b`, `--surface-2 #1c2438`, `--elevated` for popovers); keep one confident `--accent`; add `--accent-weak` tint + `--ring` focus color; add explicit `--good/--warn/--bad` semantic aliases and **fix the `.tag-amber`→`--danger` bug** (map amber tag to `--warn`).
- Add `--font-ui` and `--font-display` (see §2), `--dur` motion token.

### 2. Typography / font — `main.tsx` + `styles.css`
Self-host to avoid any external-network dependency: **`pnpm add @fontsource-variable/inter @fontsource-variable/space-grotesk`** (this is a **pnpm** project — `pnpm-lock.yaml`, README uses `pnpm install`/`pnpm dev`; do **not** use npm). Inter = UI/body (tabular-nums), Space Grotesk = display (`h1`, KPI values, brand). `import` both in `src/main.tsx` (alongside the existing `import "./styles.css"`). Set `--font-ui: 'Inter Variable', system-ui, …` and `--font-display: 'Space Grotesk Variable', …`. Apply display font to `h1`, `.kpi-v`, section eyebrows; enable `font-feature-settings:'tnum','cv01'` on numeric contexts.

### 3. Elevation, focus, motion — `styles.css`
- Apply `--sh-2` + hairline highlight to `.card/.panel`, `--sh-3` to overlays (the DataAdmin/ProjectsAdmin inline edit cards, copilot composer). Add a subtle 1px gradient border-top on cards for the "glow" feel.
- Global `*:focus-visible { outline: 2px solid var(--ring); outline-offset: 2px }`; add `transition` (bg/border/transform) on `.tab`, buttons, `.grid tr`, `select`, inputs; `::selection`; `@media (prefers-reduced-motion) { * { transition:none } }`.

### 4. Button system — `styles.css` + a few components
Introduce `.btn` base (carries all shared button styling: padding, radius, weight, transition, focus) + variants `.btn-primary` (accent), `.btn-secondary` (surface-2 + border), `.btn-ghost` (transparent), `.btn-danger` (`--bad`); `.btn-sm` is a **size-only modifier that requires `.btn`**. So every button uses `.btn` plus a variant (and optionally `.btn-sm`). Replace the inline `style={{background:'var(--danger)'}}` delete buttons with `className="btn btn-sm btn-danger"` in `DataAdmin.tsx` (`:159`) and `ProjectsAdmin.tsx` (Delete), cancel buttons → `btn btn-sm btn-secondary`. **Migrate the existing bare `.btn-sm`/`.capture button` usages** across components to the new `.btn …` convention so nothing loses base styling (or, to limit churn, keep `.btn-sm` self-sufficient by having it also apply base styling — pick one and apply consistently; the plan's intent is that no button renders unstyled).

### 5. Shared formatting util — NEW `src/format.ts`
One module exporting `money(value, currency)` (thousands + the given ISO currency as a suffix/label, dash on null), `ratio(v)` (`.toFixed(3)`, dash on null), `pct(v, dp?)`, `millions(v, currency)`, and `DASH`. Replace the inline formatters in `EvmOverview.tsx:4-5`, `CostCentreGrid.tsx:4`, `ForecastCone.tsx:4`, `StressTest.tsx:6-8`, `VarianceCard.tsx:4-5`, `ValidationPanel.tsx:4`, and format DataAdmin numeric cells (`DataAdmin.tsx:155`).

**Currency wiring (currently missing):** `App.tsx` passes only `period`/`rev` to these children (`:127-148`) — no currency. Add a `currency` prop threaded from App's already-computed `project` (`currency={project?.reportingCurrency ?? "AED"}`) to `EvmOverview`, `CostCentreGrid`, `ForecastCone`, `StressTest`, `VarianceCard`; `ValidationPanel`/`DataAdmin` are project-agnostic (default "AED"/none). **On the AED naming:** the DTO fields `cvAed`/`svAed` etc. are just field *names* — the amounts are already stored in the project's `reporting_currency`, so labeling with the passed `currency` is correct. The concrete fix is to **stop hardcoding the literal `" AED"` string** in `StressTest.tsx`/`VarianceCard.tsx` and use the prop instead; do **not** invent conversion. (If a value is genuinely AED-fixed, leave its label alone — none were found.)

### 6. Dedupe + KPI unification — `styles.css` + `ValidationPanel.tsx`
Delete the dead first `.kpi`/`.kpis` block (`:67-71`) and empty rules (`.content`, `.composer button, .suggestion:active`). Standardize on `.kpi-v`/`.kpi-l` and migrate `ValidationPanel.tsx` (`.kpi-value`/`.kpi-label`/`.kpi-sub` → `.kpi-v`/`.kpi-l` + a `.kpi-sub`). Restyle `.kpi` tiles: larger display-font value, uppercase eyebrow label, subtle inner shadow, `.good`/`.bad` accents kept.

### 7. Shell polish — `styles.css` (+ minor `App.tsx`)
- `.topbar`: tighter title lockup with display font, the health readout as a `.pill`/chip on the right.
- `.controls`: consistent `--sp` gaps, labeled select "fields" with the new focus ring; the `+ New / Manage` button → `.btn-secondary`.
- `.tabs`: restyle to an **underline/segmented** active state (active = accent underline + brighter text rather than a solid blue block), even spacing, wrap gracefully; add hover transition. Add one small breakpoint so the tab row and controls behave on ≤760px.

### 8. Empty & loading states — NEW `src/components/Loading.tsx` + `styles.css` + loading spots
Add `.skeleton` (shimmer via `@keyframes` + reduced-motion off), a `.spinner`, and a `.empty-state` (centered icon glyph + muted copy) to `styles.css`. Create **`src/components/Loading.tsx`** exporting small `<Spinner label?>` and `<EmptyState icon title hint?>` components (so markup is shared, not re-duplicated per file). Import and replace the bare "Loading…" text in the data components (`EvmOverview` "Loading EVM…", `Watchlist`, `CostCentreGrid`, `ForecastCone`, `StressTest`, `ForecastBacktest`, `ValidationPanel`, `DataAdmin`, `ProjectsAdmin`) with `<Spinner/>`, and render the empty-project message (`App.tsx`) + StressTest/Variance "unavailable" states via `<EmptyState/>`.

### 9. Chart color/legibility pass (light touch — keep SVG hand-rolled)
No chart rewrites. Just: route all chart strokes/fills through tokens; give sparklines a soft gradient fill under the line (`EvmOverview` `Spark` `:7-22`); add faint gridlines + value labels to the forecast cone (`ForecastCone` `ConeChart` `:7-39`) and value labels to the waterfall bars (`VarianceCard` `Waterfall` `:100-138`); bump chart label font from 10px. Skip tooltips/axes engines.

### Representative files
- **Heavy**: `src/styles.css` (token layer, elevation, buttons, tabs, KPI, skeleton, dedupe).
- **New**: `src/format.ts`, `src/components/Loading.tsx`.
- **Touched (pattern-repeat)**: `src/main.tsx` (font imports), `src/App.tsx` (shell classes, currency prop threading, empty-state), and per-component formatter swaps + button-class swaps + `<Spinner>` swaps across **all 12** components: `EvmOverview/CostCentreGrid/CapturePanel/PeriodsPanel/Watchlist/ValidationPanel/Copilot/DataAdmin/ForecastCone/ForecastBacktest/StressTest/VarianceCard/ProjectsAdmin` (i.e. `CapturePanel/PeriodsPanel/ForecastBacktest` are in scope too — they have buttons, loading text, and formatting).
- `package.json` + `pnpm-lock.yaml` (two `@fontsource-variable/*` deps, added via `pnpm add`).

---

## Codex Review

### Round 1 — 8 findings, all valid, all addressed
| # | Finding | Resolution |
|---|---------|-----------|
| 1 | Plan said `npm install`, but this is a **pnpm** project (`pnpm-lock.yaml`, README `pnpm install`/`pnpm dev`). | §2 + verification now use `pnpm add`/`pnpm install`/`pnpm dev`; noted do-not-use-npm. **Verified** lockfile + README. |
| 2 | `reportingCurrency` "passed everywhere" but `App.tsx` passes **no** currency prop to the children (`:127-148`). | §5 now specifies threading `currency={project?.reportingCurrency ?? "AED"}` from App's `project` into the 5 project-scoped components. **Verified** App passes only `period`/`rev` today. |
| 3 | AED-specific field names (`cvAed`/`svAed`) + hardcoded `" AED"` text could mislabel non-AED projects. | Clarified: field *names* ≠ units — amounts are already in the project's `reporting_currency`, so labeling with the passed `currency` is correct. Fix = stop hardcoding the `" AED"` string; no conversion invented. |
| 4 | Touched-files list omitted `CapturePanel`, `PeriodsPanel`, `ForecastBacktest` (they have buttons/loading/formatting). | Added all three; representative list now names all 12 components explicitly. |
| 5 | `.btn-sm btn-danger` would drop base styling if `.btn` is the base. | §4 now mandates `.btn btn-sm btn-danger` (base + size + variant) and states no button may render unstyled. |
| 6 | Verification listed only 7 items incl. "Variance" (not a top tab); app has 10 tabs. | Verification now lists all 10 real tabs and notes Variance is a card inside the Watchlist tab. |
| 7 | `--fs-h3(13u)` — `13u` is invalid CSS. | Type tokens are now valid px lengths; uppercase/letter-spacing moved to the class, not the token. |
| 8 | "shared Spinner/Skeleton" had no component file/import path → risk of CSS-only + duplicated markup. | Added NEW `src/components/Loading.tsx` (`<Spinner>`/`<EmptyState>`) with explicit import-and-replace across the loading spots. |

### Round 2 — re-review verdict: **converged**
Codex (gpt-5.5) re-reviewed the revised plan: findings 1–8 all **RESOLVED**, and **NO MATERIAL FINDINGS** raised. The plan is ready to implement.

---

## Verification (end to end)
1. `pnpm install` (new font deps), then run the stack (`/run_system`, or `pnpm dev` + the API on :5070).
2. **Visual before/after**: use the `browse` skill to screenshot **all 10 top tabs** — EVM Overview, Cost Centres, Monthly Capture, Periods & Estimate, Data Admin, Watchlist (also click a row to surface the Variance card), Forecast, Stress Test, Model & Copilot, Projects — at desktop **and** ≤760px; compare against the current flat look. Confirm elevation, font, tabs, KPIs, buttons read as intended.
3. **No regressions**: `npx tsc --noEmit` clean; `$B console --errors` empty on each tab; every chart (sparkline, cone, waterfall, heatmap, risk bar) still renders with correct data; numbers formatted consistently (currency unit present, no raw values in DataAdmin).
4. **A11y/polish**: keyboard-tab through controls → visible focus rings; check text contrast on the new ramp (WCAG AA for body); `prefers-reduced-motion` disables transitions.
5. **Scope guard**: no layout/structure change beyond tabs restyle; dark-only (no light theme leaked); backend untouched.
