# QS Cost Early-Warning — Feature Documentation

Simplified, one-page-per-feature documentation for the **QsEarlyWarning** system: a
multi-tenant ASP.NET Core 8 + React application that helps a Quantity Surveyor (QS) see
cost trouble early on a construction project.

Every amount is in the project's reporting currency (**AED** for Tower X). The analytics are
Earned Value Management (EVM): `CV = EV − AC`, `CPI = EV ÷ AC`, `SPI = EV ÷ PV`,
`EAC` = forecast final cost, `VAC = BAC − EAC`.

## The product in one line

Load a project's cost data, watch its EVM live, get an early warning **one reporting period
before** a cost centre tips from GREEN to AMBER, forecast next-period spend, review estimate
assumptions and reconciliation before award, drill into which resource an over-run is
**attributed** to, and ask all of it in plain English.

## Features

| # | Feature | What it answers | Doc |
|---|---------|-----------------|-----|
| 1 | Early-Warning Watchlist | *Which GREEN cost centres are about to tip to AMBER?* | [01-early-warning-watchlist.md](01-early-warning-watchlist.md) |
| 2 | Cost-Trajectory Forecaster | *How much will we spend next period, and what's the range?* | [02-cost-trajectory-forecaster.md](02-cost-trajectory-forecaster.md) |
| 3 | Estimate Assumption Stress Test | *Which estimate assumptions look aggressive before we award?* | [03-estimate-stress-test.md](03-estimate-stress-test.md) |
| 4 | QS Copilot | *Ask anything about the project in plain English.* | [04-qs-copilot.md](04-qs-copilot.md) |
| 5 | Variance Attribution Bridge | *Which resource is this package's variance attributed to?* | [05-variance-attribution-bridge.md](05-variance-attribution-bridge.md) |
| 6 | Model Validation Panel | *How accurate is the early-warning model, honestly?* | [06-model-validation-panel.md](06-model-validation-panel.md) |
| 7 | Live EVM Dashboard | *What is the project's cost health right now?* | [07-live-evm-dashboard.md](07-live-evm-dashboard.md) |
| 8 | Authoring Workflow | *How do we open/close periods and capture progress & cost?* | [08-authoring-workflow.md](08-authoring-workflow.md) |
| 9 | Project Management | *Create, import, switch, and delete projects.* | [09-project-management.md](09-project-management.md) |
| 10 | Data Administration | *Governed CRUD over the underlying tables.* | [10-data-administration.md](10-data-administration.md) |
| 11 | Multi-Tenant Security | *How is each project's data isolated per user?* | [11-multi-tenant-security.md](11-multi-tenant-security.md) |
| 12 | IFC → BOQ Element Register | *Click an element: what does the bill say about it?* | [17-ifc-boq-element-map.md](17-ifc-boq-element-map.md) |

### Deep dives

- [Idea 1 — Early-Warning Classifier: How It Was Actually Built](12-idea-1-implementation.md) —
  engineering reality for Feature 1: spec→shipped departures, the frozen `RuleRiskScore@v1`, a
  step-by-step **math walkthrough** (gap, score, fitting, precision@k) with a worked example, the
  leakage-safe rolling-origin training, and the three ways a QS uses it (tab, API, copilot).
- [Idea 2 — Incremental-Spend Forecaster: How It Was Actually Built](13-idea-2-implementation.md) —
  engineering reality for Feature 2: the reframe from final-cost EAC to **short-horizon incremental
  spend**, the ridge P50 in BAC-fraction space, the **split-conformal** P10–P90 band, the joint
  residual-path cost cone, the **grouped rolling-origin** back-test against four baselines, and the
  validated-vs-directional split across the tab, API, and copilot.
- [Idea 3 — Estimate Assumption Stress Test: How It Was Actually Built](14-idea-3-implementation.md) —
  engineering reality for Feature 3: the double reframe away from a should-cost tautology, the
  deterministic engine's **three separated output classes** (reconciliation tie-out / cohort-gated
  assumption flags / retrospective peer benchmark), the load-bearing **Output-Norm divisor**, the
  leave-one-out + 5-peer-minimum guards, and the tie-out test suite that is the engine's credibility.
- [Idea 4 — QS Copilot: How It Was Actually Built](15-idea-4-implementation.md) —
  engineering reality for Feature 4: the **"tools compute, model narrates" code boundary**, the MAF
  `ChatClientAgent` over Claude Sonnet 5, the 10 read-only tools with a `sources` provenance block, the
  `sum(EV)/sum(AC)` and validated-vs-directional rules, the RLS-before-LLM tenancy boundary, and the
  independent-ground-truth eval (21 offline tests + an opt-in live routing eval).
- [Idea 5 — Variance Attribution Bridge: How It Was Actually Built](16-idea-5-implementation.md) —
  engineering reality for Feature 5: the reframe away from a quantity-vs-rate split of CV, the **two
  honest lanes** (cost/efficiency CV by resource + schedule SV), the additive **`Σ CV_r + residual == CV`
  tie-out**, the estimate-share allocation with its assumption badge + evidence-needed field, the
  `EP-`/live gates, and the watchlist click-through + copilot tool that consume it.

## How to read a feature doc

Each doc is intentionally short and follows the same shape:

- **What it is** — one paragraph.
- **Who it's for** — the QS role that uses it.
- **How it works** — the mechanics, plainly.
- **API** — the endpoints.
- **UI** — where it lives in the app.
- **Guarantees & limits** — what is honest/validated vs. directional.
- **Codex Review** — findings from the OpenAI Codex review pass and their resolutions.

## Codex Review

**Round 1 (CHANGES REQUESTED) — resolved.**

- _"Every read is authorized" overstates it_ — `/health`, `/validation-summary`, and the
  `/entities` registry are global/unauthenticated. **Fixed:** the one-liner now says "review
  estimate assumptions and reconciliation before award" and "attributed," the diagram says
  "project-data reads," and a note lists the global endpoints.
- _Variance shown as fact_ — **Fixed:** the table and one-liner now say "attributed."
- _Pre-award claim too broad (Class 3 is retrospective)_ — **Fixed:** the summary now limits the
  pre-award scope to assumption checks and reconciliation.

**Codex verdict — APPROVED (round 3):** all findings resolved, no new issues.

## System shape

```
Workbook / DB  →  Project snapshot (per tenant, RLS-scoped)
                    ├─ Core analytics (rule scorer, forecaster, stress tester, variance attributor)
                    ├─ Web API (api/v1/*)   ← project-data reads are authorized before they run
                    └─ React SPA (tabbed dashboard) + QS Copilot
```

A few endpoints are **global, not tenant-scoped**: `/api/v1/health` and
`/api/v1/validation-summary` report the process's startup Tower X workbook model, and the
`/api/v1/entities` registry is static metadata. Everything that reads *project data* is
authorized first (see [Multi-Tenant Security](11-multi-tenant-security.md)).

The data model, EVM identities, and gotchas are documented in the repo root
(`DATA_DICTIONARY.md`, `data/README.md`, `CLAUDE.md`). This folder documents the **features**
built on top of that data.
