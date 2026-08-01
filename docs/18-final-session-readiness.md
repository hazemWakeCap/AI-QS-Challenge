# Final Session Readiness — Saturday 1 August

**Written:** 2026-07-29 · **Source:** [`FINAL_SESSION.md`](https://github.com/wakecap/AI-QS-Challenge/blob/main/FINAL_SESSION.md) (upstream `wakecap/AI-QS-Challenge`)

This is a recommendations document, not a feature doc. It answers one question the team asked:
**does what we have already built cover what the judges expect, or do we need more features — and
if so, which ones would attract marks?**

The short answer, up front:

> **We clear the bar on five of the seven judged dimensions today. More features is not the
> highest-value move.** The two dimensions carrying **35 of 100 marks** — Business Impact and Team
> Collaboration — are won by framing and behaviour, not by code. The single highest-value build is
> not another analytic; it is a **deliverable that comes out the other end** of what we already
> compute.

---

## 1. What Saturday actually asks for

`PROBLEM.md` has not changed. `FINAL_SESSION.md` adds four things we did not previously know.

| | |
|---|---|
| **The day is an open build with That Open Engine** | Antonio, founder of That Open Company, joins **online 11:30–12:15 only**. He is the only live access to the person who built the engine. |
| **Build time is ~105 minutes** | 13:15 → 15:00. Then Kahoot at 15:00, **20-minute final presentations** at 15:30. |
| **The rubric is now explicit** | GPMF jury, 100 marks — see §2. |
| **Prep is expected** | Have a model already downloaded and opened. Have the existing work **running**. Come with questions for Antonio. |

Two notes the brief calls out by name:

- **Technical Design includes *engineering mindset*** — "how you went about it, from delegating to AI
  through to thinking about your own evaluation."
- **Business Impact is the heaviest single weight** — "whatever you build, be able to say why a QS
  would care."

And the bar, unchanged: *would a QS actually use what you built?*

### Where we already stand against the prep bar

Most of the "worth prepping before Saturday" list is already done, which is the single biggest
advantage we have going in:

| Prep item | Status |
|---|---|
| Have a model, downloaded and opened | ✅ `public/models/school_str.ifc` — 8.6 MB, **git-tracked**, Autodesk Revit 2024 → IFC4 via ODA (`rstadvancedsampleproject`), 1,526 elements, 5 storeys |
| That Open Engine working | ✅ `@thatopen/components@3.4.8`, `@thatopen/fragments@3.4.7`, `web-ifc@0.0.77`, `three@0.185.1` — WASM and worker **bundled locally**, no CDN at runtime |
| Existing work running | ✅ `/run_system` brings up API :5070 + dashboard :5173 + Postgres `qs_phase1` |
| Questions for Antonio | ⚠️ Four real ones exist — but they are buried in code comments. See §7. |

Teams that spend Saturday getting an IFC to load will be spending their 105 minutes on what we
finished last week.

---

## 2. Coverage verdict — the rubric, line by line

| Dimension | Marks | Evidence in the repo | Verdict |
|---|---|---|---|
| **Business Impact & Value Creation** | **20** | Watchlist flags a GREEN centre **one period before** it tips AMBER; `precision@5 = 45%` vs `35%` for the best CPI-native baseline, macro-mean over 8 rolling-origin folds; model-vs-bill quantity variance fires *before* the pour | **Strongest capability, weakest framing.** No AED-denominated value claim exists anywhere |
| **Problem Relevance & Clarity** | 15 | Deck slides 2–3 map directly onto `PROBLEM.md`'s two QS questions; 12 documented features in `docs/` | ✅ Covered |
| **Technical Design & Architecture** | 15 | .NET 8 layered solution (Domain ← Infrastructure ← Core ← Web.API + Agent), **10** Postgres migrations with real RLS, **151** xUnit facts, frozen `RuleRiskScore@v1`, leakage-safe rolling origins, `plan/` + `docs/` + three Codex review rounds | ✅ Covered — but **under-shown** |
| **Presentation & Communication** | 15 | 18-slide `presentation/qs-cost-demo.html` + PDF + `tower-4d-build.mp4` | ⚠️ Covered, but **carries four stale numbers and two hard-coded AED figures** — §3 |
| **Team Collaboration & Engagement** | 15 | Cannot be built. Earned on the day, in front of the jury and in the Antonio window | ⚠️ **At risk if unplanned** |
| **Innovation & AI Application** | 10 | 13-tool MAF copilot over `claude-sonnet-5`, "tools compute, model narrates", 21 offline evals + opt-in live routing eval, out-of-scope guardrail, RLS-before-LLM | ✅ Covered |
| **Prototype Quality & Demonstration** | 10 | Two 3D tabs, real Revit IFC4 export, 4D build playback, element picking → bill, deterministic headless video render | ✅ Covered — **has stage risks**, §9 |

**Read the table this way.** Technical Design, Innovation and Prototype Quality (35 marks) are
already earned; nothing built on Saturday will move them much. Problem Relevance (15) is settled.
That leaves **Business Impact (20), Presentation (15) and Team Collaboration (15) — 50 marks — as
the entire realistic upside**, and only one of those three is a code problem.

---

## 3. Where marks are leaking right now

All verified against the repo on 2026-07-29. All cheap to fix, none require Saturday's build time.

### Stale numbers in the deck — every one understates us

| Deck says | Reality | Where |
|---|---|---|
| "12 read-only tools" | **13** | `ClaudeQsCostCopilotAgent.cs` |
| "12 tabs over one coherent product" | **15** | `App.tsx` `TABS` |
| "9 migrations, row-level security" | **10** | `db/migrations/` |
| "~100 xUnit tests" | **151** | `[Fact]`/`[Theory]` under `tests/` |

`QsEarlyWarning/README.md` also still claims **"27 tests"**.

A judge who checks and finds the number is *higher* than claimed will not deduct — but nobody will
check. These are marks we are simply choosing not to claim.

### Two AED figures on stage are hard-coded HTML, not live

Slide 13: *"19.96M AED sits in AMBER centres and is not yet spent"*, *"only 8.1M left to save"*,
*"43.5M unspent"*. Slide 14: *"4.64M AED priceable at Tower X's rates"*.

The element counts beside them (375 beams, 619 rebar, 58% measurable) **are** corroborated by
`TakeoffPricingTests`. The money is asserted in HTML and not asserted anywhere in code. Re-derive
them from a live run before presenting, or make the slide pull them.

### A demo-risk one-liner

`LocateCostRisk` is registered as the 13th copilot tool but **appears zero times in
`CopilotPrompts.System`**. The routing guidance names the other twelve, so the model can only find
it through its `[Description]` attribute. If a judge asks the copilot a spatial question — *"where
is the risk?"*, exactly the question the 3D tabs invite — routing is a coin flip. One line.

### Rotate the key before you screen-share

`src/QsEarlyWarning.Web.API/appsettings.Development.json` holds a **live Anthropic API key in
plaintext**. It is correctly gitignored and confirmed absent from git history — but it is on disk,
and Saturday involves projecting a laptop and possibly opening an editor. Rotate it.

### The missing sentence

Nowhere in the deck, the docs, or the app does a sentence exist of the form *"this is worth AED X to
a project like Tower X."* Business Impact is 20 marks. See §5.4 for how to build that sentence from
data we already have, rather than inventing one.

---

## 4. Do we need more features?

**No new analytics.** Six of them are already backtested, tied-out, and documented; a seventh added
in 105 minutes would be neither.

The real gap is a different shape. **Everything this system computes stops at a screen.** The QS
looks at a watchlist, a cone, a painted building — and then goes and does their actual job somewhere
else. Nothing comes out the other end that a QS *files*.

That gap is precisely what the brief's own prompt list points at:

> *"Generate drawings or documentation from a model."*
> *"Make something a QS could actually open on a Monday morning."*

So: not more features. **One deliverable.**

---

## 5. The recommendation — the Interim Valuation Pack

> **Build a per-storey interim valuation, generated from the model, that a QS could file.**

### 5.1 Why this one

- It answers **two** of the brief's prompts at once, in the brief's own words.
- It attacks **Business Impact (20)** — the heaviest weight and our weakest framing — by turning an
  analytic into a document with money on it.
- The valuation is *the* recurring QS deliverable. "Would a QS actually use this?" stops being a
  question when the output is the thing they submit monthly.
- Critically: it is **assembly, not invention**. Almost every part exists.

### 5.2 The parts that already exist

| Need | Already built |
|---|---|
| Element → BOQ item → cost centre | `data/ifc_boq_map.csv` — 2,034 rows, **1,526 elements, 1,127 mapped (74%)**, reaching 8 cost centres |
| GlobalId → live model | `model/ifcElementMap.ts` `buildElementIndex()` via `getLocalIdsByGuids()` |
| Grouping by storey, state at a period | `model/ifcSequence.ts` — `STOREY_RANK`, `statesAt(t, centresByPeriod)`, `frameAt()` |
| Rates, unit guard, tie-out | `Core/Model/TakeoffPricer.cs` + `TakeoffRateMap.cs` |
| Posed, cost-painted capture | `model/cameraPath.ts` `poseAt()` + `useBuildStage.ts` `renderFrame()` + `model/ifcPaint.ts` |
| Overlay compositing | `model/videoCompositor.ts` (Canvas2D, already solves the "captureStream only sees the GL canvas" problem) |
| Print/PDF | the deck's existing print-CSS approach |

### 5.3 Scope for the 105 minutes

A new tab plus an export. **No backend change, no schema change.**

1. **Per storey, per period** — a valuation table: elements, the BOQ items they consume, quantity,
   unit rate, % complete this period, amount this period, cumulative to date. Storeys ordered by
   `STOREY_RANK` (Sub Level → 01 Entry → 02 → 03 → Roof), which the sequence module already does.
2. **A posed, cost-painted storey view per storey**, captured through the existing `poseAt` +
   `renderFrame` path. **Deliberately not a true 2D plan or section export** — that is the risky
   part of the engine, and at valuation fidelity a QS cannot tell the difference. Attempt the real
   plan view only as a stretch (§5.5).
3. **The caveats travel on the document, not beside it.** On this model that means printing, on the
   pack itself: **0 Direct links / 100% Grouped** (no element in a real Revit structural export
   carries a cost code in its property sets), and the **26% scope gap** — 375 `IFCBEAM`, 22
   `IFCMEMBER`, 2 `IFCPLATE` — as an explicit unpriced residual rather than a silent omission.
   This is the same discipline as `unmappedBac` in the cost map, and it is the reason a QS would
   trust the document at all.
4. **The existing "This is not Tower X" provenance badge stays on it.** The pack demonstrates that a
   rate library and a cost plan travel to any model you can measure — never that this school holds
   Tower X's budget.

### 5.4 While you are in there — the sentence that earns the 20 marks

The valuation pack gives you the natural home for the missing money claim, and it can be **derived,
not asserted**:

> Over the 8 backtestable origins, the rule puts 5 centres in front of the QS each period at 45%
> precision — call it ~2 real tips caught per period, each **one period earlier** than the strongest
> CPI-native heuristic. The value is the **unspent** portion of those centres at the moment they were
> flagged, which the app already computes as "unspent exposure."

Compute that number from a live run and put it on the slide with its assumption stated. That is a
defensible AED figure with a backtest behind it — which is a different thing entirely from a
confident guess, and the jury contains people who know the difference.

### 5.5 Stretch — and the stretch has a consolation prize

If the storey capture lands early, attempt a real 2D plan through That Open's clipping / plan-view
path. **If it does not work, that is the finding** — and findings are explicitly worth marks (§6).
There is no way to lose this bet.

### 5.6 Fallbacks — only if the primary is blocked

1. **Promote the model-vs-bill quantity variance into the watchlist as a pre-pour warning class.**
   Already computed by `TakeoffPricer.CompareToBoq()`, grouped by BOQ item, with the two refusals
   already built in (never compare an item with a missing/zero bill quantity; never call the result
   an overrun on a foreign model). Needs surfacing only. This is the only warning in the system that
   fires *before* money moves — every other one is downstream of a booked cost.
2. **Persist the `.frag` + element index server-side** (Phase 3 of `plan/bim-gap-analysis-and-integration-plan.md`).
   Unblocks the copilot answering *"which elements"* rather than *"which zone."* Higher effort,
   defers cleanly, and the browser-converts / API-stores design is already decided.

---

## 6. The engine findings — marks we have already earned and are not claiming

The brief says, in as many words:

> *"Explore what the engine can't do yet — and say so. That's a finding too."*

We have four. Every one was found the hard way, is documented, and is currently sitting **in a code
comment where no judge will ever see it**:

| Finding | Where |
|---|---|
| `editor.createElements` renders geometry, but the items never enter the queryable index — `getLocalIds()` / `getBoxes()` come back empty, so `setColor` / `highlight` / fit-to-model **silently no-op**. (This file already contains an open question addressed to Antonio.) | `model/towerGenerator.ts` |
| `FragmentsModel.raycast` returns null against a loaded IFC in 3.4.7. `SimpleRaycaster.castRay` → `FastModelPickers.getFullPick` is the path that works. | `model/ifcPick.ts` |
| A real Autodesk Revit 2024 → IFC4 (ODA) export carries **zero `IfcElementQuantity`**. Quantities exist only inside Revit's own parameter groups — and on this file those groups are **in Spanish** (`Volumen`, `Área`). The textbook parser returns nothing on real exporter output. | `model/ifcMeasure.ts` |
| Every mutation reaches `updateVirtualMeshes → restart()`, so 4D frame cost is proportional to **model size, not to the delta**. Worked around by batching inside `model.frozen = true/false`. | `model/ifcPaint.ts` |

**Recommendation: one slide, titled as a finding, not a complaint.** Frame it as "what we learned
about the engine by pushing it," with the workaround beside each. This is the cheapest Innovation +
Technical Design mark available and it costs **zero** build time — the work is already done.

---

## 7. Questions for Antonio — 11:30 to 12:15, the only window

Ordered so they land as findings rather than support requests. The first four come straight from §6,
which means we arrive having done the work rather than asking to be taught.

1. **Programmatic geometry and the item index.** `editor.createElements` renders, but the items never
   appear in the queryable index — `getLocalIds()` and `getBoxes()` return empty, so per-item colour
   and fit-to-model no-op. Is Fragments strictly an *import* target, or is authored geometry meant to
   be queryable?
2. **Raycasting a loaded IFC.** `FragmentsModel.raycast` returns null in 3.4.7; `SimpleRaycaster.castRay`
   works. Bug, or has the picking entry point moved?
3. **Batched mutation.** Every `setColor` / `setVisible` restarts the whole-model virtual-mesh sweep,
   so a 4D frame costs O(model) rather than O(delta). Is `model.frozen` the intended answer, or is
   there an incremental path?
4. **QTO on real exporter output.** Revit/ODA exports carry no `IfcElementQuantity` and localise the
   parameter groups. Is there a canonical quantity-take-off path in That Open, or is pset scraping
   with a synonym table what everyone actually does?
5. **A .NET backend wanting persisted `.frag`.** `IfcImporter` is Node-only. Is a Node sidecar the
   intended architecture, or is browser-converts / API-stores an acceptable pattern?
6. **2D drawing generation.** What is the supported path for plan and section generation from
   Fragments today, and can it export SVG or DXF?
7. **Writing back to elements.** Is there a supported way to attach and persist custom data — a cost
   code, a valuation state — onto elements, so the model becomes the record rather than a viewer?

Question 6 gates the §5.5 stretch — ask it early in the window.

---

## 8. Presentation plan — 20 minutes, weighted to the marks

Weight the running order by the rubric, **not** by how hard each part was to build. The stress test
and the authoring workflow took real work and should get about ninety seconds between them.

| Min | Beat | Rubric target |
|---|---|---|
| 0–2 | The problem — a QS spots the overrun weeks too late | Problem Relevance (15) |
| 2–4 | The insight — flag it **one period** before it tips | Problem Relevance |
| 4–7 | **The Proof tab: we let the app grade itself.** 45% vs 35%, per fold, on real history | Business Impact + Technical Design |
| 7–11 | **The valuation pack** — model → storey → priced → filed. *The money sentence lands here.* | **Business Impact (20)** |
| 11–14 | The building: 3D Cost X-Ray, IFC take-off, 4D build | Prototype Quality (10) |
| 14–16 | **How we worked** — delegating to AI, the eval harness, Codex review rounds, the experiment we nearly got wrong | Technical Design *(engineering mindset)* |
| 16–18 | **What the engine can't do yet** — the four findings | Innovation (10) |
| 18–20 | Honest about limits, and what we'd build next | Business Impact |

**Assign the beats by name, out loud, before 15:30.** Team Collaboration is 15 marks and is judged on
what the jury sees. A single presenter running all twenty minutes scores badly on a dimension worth
as much as Technical Design. Same logic applies to the Antonio window: decide now who asks which of
the seven questions in §7.

---

## 9. Demo risk register

| Risk | Mitigation |
|---|---|
| **Headless / SwiftShader Chromium cannot render the viewer** — `THREE.WebGLRenderer: Error creating WebGL context` | Present from **real Chrome with GPU access**. The video renderer needs *full* Chrome, not `chrome-headless-shell` |
| The 8.6 MB IFC is **re-parsed in the browser on every page load** and the index is thrown away on unmount | **Open the IFC tab before you present.** Never load it cold in front of the jury |
| Postgres must be up (`qs_phase1`) or every project view is empty | `/run_system` preflights this. `QsEarlyWarning.Db.Tests` additionally need Docker |
| `ANTHROPIC_API_KEY` unset → copilot answers "not configured" | Set it *and* confirm with one live question before you start |
| **`npm install` fails** on the `@thatopen/fragments` dependency tree | **pnpm only.** Do not let anyone "fix" it with npm on the day |
| Live 3D dies on stage | `presentation/tower-4d-build.mp4` is a deterministic pre-render — cut to it and say that is what you are doing |

---

## 10. Pre-Saturday checklist

Three days: Wed 29 Jul → Sat 1 Aug. Nothing here needs Saturday's build window.

- [ ] **Rotate the Anthropic key** in `appsettings.Development.json` before any screen-share
- [ ] Fix the four stale deck numbers → **13 tools · 15 tabs · 10 migrations · 151 xUnit tests**
- [ ] Fix `QsEarlyWarning/README.md` — "27 tests" → 151
- [ ] Re-derive or make live the slide 13/14 AED figures (19.96M / 8.1M / 43.5M / 4.64M)
- [ ] Add `LocateCostRisk` to `CopilotPrompts.System` — one line
- [ ] Compute the **unspent-exposure-at-flag-time** figure (§5.4) and put it on the deck
- [ ] Write the **engine findings slide** (§6) — the content already exists in code comments
- [ ] **Download a second IFC from a different exporter** — the buildingSMART Duplex is a good choice
      precisely because, unlike ours, it *does* carry `BaseQuantities`. Proves the "point it at any
      model" claim live, and makes the §6 quantity finding land as a comparison rather than an excuse
- [ ] Dry-run `/run_system`, then `/render-video`, end to end on the presenting laptop
- [ ] Agree who asks which of the seven Antonio questions
- [ ] Rehearse the 20 minutes **once, against a clock**

---

## Guarantees & limits

- Every count in this document was verified against the repo on **2026-07-29**: 13 copilot tools,
  15 tabs, 10 migrations, 151 xUnit facts, 2,034 CSV rows / 1,526 elements / 1,127 mapped (74%),
  399 unmapped (375 `IFCBEAM` + 22 `IFCMEMBER` + 2 `IFCPLATE`).
- The `45% / 35%` precision@5 figures are quoted from `docs/06` and `docs/12` and are **Tower
  X-specific**, macro-mean over 8 rolling-origin folds. They are not a general claim.
- The slide 13/14 AED figures are **not** verified — that is the point of the §3 action.
- §5 is a recommendation, not a plan of record. Nothing in it has been built.
