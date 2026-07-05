# Idea 5 — Variance Attribution Bridge (formerly Root-Cause Decomposer)

**One-line pitch.** Turn a red flag into an attribution: don't just say a package is over budget — say
*which resource category* drives the cost variance and whether schedule is off. It is an attribution
bridge, not a proven price-vs-productivity cause (this data cannot separate those).

**The QS pain it kills.** An alert ("BCC-STR-12 is AMBER, CV −85k") tells the QS *that* there's a
problem, not *what to do*. The QS still has to dig through resource lines to find whether it's a
labour-productivity collapse, a material price spike, or just more work than planned. This does that
dig automatically: it names the dominant cost-variance contributor and flags whether schedule is off —
the cause itself stays a hypothesis for the QS to confirm. It is the "why" behind Idea 1's "which."

**Approach.** An EVM variance bridge, decomposed by resource. Do NOT try to split `CV_AED` itself
into a quantity part and a rate part — that framing is wrong. `CV_AED = EV − AC` is measured *at the
earned quantity*, so it is already a pure rate/efficiency variance (it has no quantity term). The
"did more/less work than planned" effect lives in `SV_AED = EV − PV`, a different lane. Report two
honest lanes:
- **Rate/efficiency lane (this is CV).** `CV_AED = EV − AC`, decomposed by resource:
  `CV = Σ_r (EV_r − AC_r)` where `EV_r = EV_AED × norm_share_r` (the norm-implied resource share
  from `4_ESTIMATE_DATASHEET`) and `AC_r` is the actual split (`AC_Material/Manpower/Equipment/
  Subcontract_AED`). Each resource's `EV_r − AC_r` is its contribution, and they sum to `CV_AED`
  exactly. Then compare actual cost per earned unit (`AC_r ÷ earned qty`) to the norm-implied budget
  per unit to *size* the gap: "manpower is the largest CV contributor, running ~1.8× its norm-implied
  budget." **Do not claim price vs productivity.** This sheet has only the four `AC_*_AED` category
  totals and whole-package quantities — no labour hours, no per-resource quantities, no purchase rates
  (verified) — so "1.8×" cannot be split into rate vs efficiency. Name the category; label any cause a
  hypothesis for the QS to confirm.
- **Schedule/progress lane (this is SV).** `SV_AED = EV − PV`, plus `Earned_Qty_Period` vs
  `Planned_Qty_Period` (same grain — never cumulative-vs-period; assert this in the tests), shows
  progress running ahead of or behind plan. Call it schedule/progress, not
  scope — SV alone does not prove physical quantity growth. Show it alongside CV, not folded in.
- Output a fact-first line with the cause flagged as a hypothesis: "Over by 85k: progress on-plan
  (SV ≈ 0); **manpower is the dominant cost-variance contributor at ~1.8× its norm-implied budget.**
  Likely cause, to confirm with labour hours/rates: productivity or wage rate."

**Data used.** `9_HISTORICAL_DATA` for actuals and variance (`CV_AED`, `EV_AED`, `PV_AED`, `SV_AED`,
`Earned_Qty_Period` and `Planned_Qty_Period` for the same-grain schedule lane, `Earned_Qty_Cumul` for
live-row gating / cumulative unit cost, the four `AC_*_AED` splits, `BAC_AED`, `Budget_Qty`,
`Direct_Unit_Cost_AED`),
joined by `Package_Code` to the norm-implied resource mix from `4_ESTIMATE_DATASHEET`
(`Resource Type`, `Resource Cost (AED)`). Header on **row 5** of the historical sheet; **row 4** of
the estimate datasheet. The estimate datasheet is already Output-Norm corrected (see `data/README.md`),
so aggregating its `Resource Cost (AED)` by `(Package, Resource Type)` gives the correct expected split
with no extra math.

**How you'd judge it's good.** Two checks, both cheap.
1. **Bridge ties out (already verified).** The full waterfall `PV → EV (SV lane) → AC (CV lane, by
   resource)` must reconstruct the reported numbers. On this data the identities hold exactly:
   `CV_AED = EV − AC_cumulative` (max diff 0), `SV_AED = EV − PV` (max diff 0), and the four resource
   splits sum to `AC` on ~1,500 of 2,076 live rows (the rest are zero/NOT STARTED). Assert these in
   code; if any package fails to tie out, show it as "unexplained residual," never hide it.
2. **Contributor face-validity.** Hand-label the dominant resource contributor on ~15–20 clearly-driven
   historical overruns and check the bridge identifies the same category. This validates *attribution*
   agreement, not causal agreement. Face validity with a QS matters as much as any number here.

**What the QS sees.** A **variance waterfall** per flagged package: PV → SV effect → EV → each
resource's CV contribution → AC, with the biggest bar highlighted and a one-line verbal attribution.
Two honesty markers sit on the card: an **"assumption-based attribution" badge** (the EV→resource
allocation uses estimate shares, not measured actuals) and an **"evidence needed to confirm cause"**
field naming what would prove it (e.g. labour hours + wage rates for a manpower variance, invoices +
quantities for a material one). The demo is the click-through: from a red row in Idea 1's watchlist to
this attribution card — one example **cost-contributor-driven** (a resource category dominates CV) and
one **schedule-driven** (SV off, CV small). Also a natural tool under the Idea 4 copilot.

**Build effort for a hackathon.** Medium. The math is arithmetic once the resource mix is aggregated
(the join is clean, see below). Most effort is the two-lane framing and the waterfall chart.

**Risks / gotchas.**
- **Do not sell "quantity vs rate split of CV."** `CV_AED = EV − AC` is the cost (efficiency) variance
  measured *at the earned quantity* — it has no quantity term to split out (quantity/schedule lives in
  the SV lane). And on this data it cannot be split into price vs productivity either: there are no
  labour hours, per-resource quantities, or purchase rates. Selling a quantity split of CV is a math
  error a sharp QS will catch. Use the SV lane for quantity.
- **Junk block in the historical sheet.** Rows ~2078–2090 are a stray monthly `AC_Cumul` roll-up
  (non-`EP-` package codes, null `BCC_ID`). Filter to `Package_Code` starting `EP-` before anything.
- **Join is clean, not the crux (audited).** All 68 `EP-` packages in `9_HISTORICAL_DATA` match a
  package in `4_ESTIMATE_DATASHEET`, covering 99.4% of package-bearing rows. The earlier worry that
  the join wouldn't be clean did not hold — spend the saved time on the framing and the tie-out.
- **NOT STARTED / zero-earned rows** have no meaningful unit cost — the `AC_r ÷ earned qty` rate blows
  up. Gate on `Earned_Qty_Cumul > 0` and only diagnose live packages.

## Codex Review — Findings and Recommendations (2026-07-05)

> **Checked 2026-07-05 (Claude): confirmed — corrected the spec above.** Verified that `9_HISTORICAL_DATA`
> carries only the four `AC_*_AED` category totals plus whole-package quantities (no labour hours,
> per-resource quantities, or purchase rates), so price cannot be separated from productivity. The
> "manpower 1.8× norm rate — productivity, not price" claim is downgraded to "largest CV contributor at
> ~1.8× its norm-implied budget", the SV lane is relabelled schedule/progress (not scope), and causes are
> now flagged as hypotheses. Codex's rename to **Variance Attribution / Variance Bridge** (rec #1) is the
> accurate framing.

> **Codex follow-up (2026-07-05) — core correction handled, document-wide cleanup still required.**
> The title remains **Root-Cause Decomposer**; the pain statement still promises an actionable cause;
> the risks still call CV a rate variance; the demo still asks for “rate-driven” and “scope/SV-driven”
> examples; and the CEO review still says quantity/schedule, scope, productivity, and “manpower 1.8×
> norm rate.” These contradict the corrected approach and will misdirect an implementation agent.
> Rename the component throughout, replace causal labels with attribution/hypothesis language, and add
> the assumption badge plus evidence-needed field to the actual deliverable specification.

> **Resolved 2026-07-05 (Claude):** propagated through the operative spec — renamed the product to
> **Variance Attribution Bridge**; reframed the QS pain to name the dominant cost-variance contributor
> with cause as a hypothesis; corrected the Risks to call CV the cost (efficiency) variance at earned
> quantity (unsplittable into price vs productivity here); swapped the What-the-QS-sees/demo examples to
> cost-contributor-driven vs schedule-driven and added the assumption-based-attribution badge plus
> evidence-needed-to-confirm-cause field; and surgically fixed the CEO Review appendix (scope→schedule,
> quantity/rate split, "1.8× norm rate — productivity not price", verdict wording).

> **Codex final check (2026-07-05): mostly resolved; residual operative wording remains.** The one-line
> pitch still opens with “diagnosis,” the pain section says the tool hands over an “actionable cause,”
> and the recommended-deliverable rationale still calls it a “root-cause view.” Replace those with
> **attribution**, **review hypothesis**, and **variance-attribution view** respectively. The formulas,
> evidence limitations, assumption badge, and evidence-needed field are otherwise correctly scoped.

> **Resolved 2026-07-05 (Claude):** replaced the residual operative wording — pitch "diagnosis" →
> "attribution", deliverable "root-cause view" → "variance-attribution view", and "cause card" →
> "attribution card" (both spots). The pain section already reads as attribution + hypothesis.

> **Codex re-review (2026-07-05): one grain error remains.** The schedule/progress lane compares
> `Earned_Qty_Cumul` with `Planned_Qty_Period`. A cumulative quantity cannot be compared directly with
> a single-period quantity. Compare `Earned_Qty_Period` with `Planned_Qty_Period`, or compare
> `Earned_Qty_Cumul` with a cumulative sum of planned quantity through the same period. Add a same-grain
> assertion in the decomposition tests so this mismatch cannot reach the narrative.

> **Resolved 2026-07-05 (Claude):** fixed the grain mismatch — the SV lane now compares
> `Earned_Qty_Period` vs `Planned_Qty_Period` (same grain), with a same-grain assertion called out for
> the tests.

> **Codex verification pass (2026-07-05): calculation fixed; data contract needs one field update.**
> Add `Earned_Qty_Period` to **Data used**. That section currently lists only `Earned_Qty_Cumul`, so an
> implementation agent following the declared inputs would not load the field required by the
> corrected period-vs-period schedule lane. Keep cumulative earned quantity only for live-row gating or
> cumulative unit-cost views.

> **Resolved 2026-07-05 (Claude):** added `Earned_Qty_Period` (and `Planned_Qty_Period`) to **Data used**
> for the same-grain schedule lane; `Earned_Qty_Cumul` is kept, scoped to live-row gating / cumulative
> unit cost.

> **Codex final consistency review (2026-07-05): one terminology cleanup remains.** In **How you'd
> judge it's good**, replace “hand-label the dominant cause” and “the decomposer names the same driver”
> with “hand-label the dominant resource contributor” and “the bridge identifies the same category.”
> The available data can validate attribution agreement, not causal agreement.

> **Resolved 2026-07-05 (Claude):** fixed. The face-validity check now reads "hand-label the dominant
> resource contributor" and "the bridge identifies the same category" — attribution agreement, not causal.

### Findings

- The two-lane CV/SV bridge is mathematically defensible and useful for variance attribution.
- The available resource cost splits can identify the category contributing most to variance, but
  they cannot distinguish resource price, productivity, overtime, crew mix, waste, or cost-coding
  effects. Actual labour hours, resource quantities, purchase rates, and usage records are absent.
- Consequently, a statement such as "manpower is 1.8× norm—productivity, not price" is not supported
  by this dataset. The defensible statement is "manpower is the largest cost-variance contributor."
- SV shows earned value versus planned value. It can indicate schedule/progress divergence, but it does
  not independently prove scope growth or a physical quantity cause.
- Allocating EV to resources using estimate shares is a useful accounting bridge, but the resulting
  attribution depends on that allocation assumption and is not a causal root-cause analysis.

### Recommendations for the implementation agent

1. Rename the component **Variance Attribution** or **Variance Bridge**, not root-cause diagnosis.
2. Restrict generated narratives to observed facts: dominant resource contribution, CV, SV, trend,
   and the estimate-share allocation used. Label causal explanations as hypotheses requiring review.
3. Show the allocation formula and an "assumption-based attribution" badge beside the waterfall.
4. Do not label SV as scope variance. Call it schedule/progress variance unless independent change-order
   or quantity-growth data is added.
5. Add an "evidence needed to confirm cause" field—for example labour hours and wage rates for a
   manpower variance, invoices and quantities for a material variance.

## Recommended deliverable

**A component, not an app** — a Python decomposition module + a waterfall chart, embedded where the QS
already is.
- **Form:** a decomposition module (`decompose.py`) that takes a `(BCC_ID, Period_ID)`, returns the
  two-lane bridge (CV by resource + the SV lane) with the tie-out assertion, and a **waterfall chart**
  component (Plotly) rendering PV → SV → EV → per-resource CV → AC with the one-line verbal attribution,
  the assumption-based-attribution badge, and the evidence-needed-to-confirm-cause field. It
  is not shipped standalone: it is the **click-through from Idea 1's watchlist** (red row → attribution card)
  and a **tool under Idea 4's copilot** ("explain BCC-STR-12's overrun").
- **Why this form:** subtraction default — a QS reaches a variance-attribution view *from* a flag, never as a
  destination. Building it as an embeddable module + chart, rather than its own dashboard, is what lets
  Ideas 1 and 4 both consume it with no rework, and keeps the tie-out logic in one tested place.
- **Not a Claude skill on its own.** The decomposition is deterministic arithmetic; the LLM's only role
  is narrating the finished attribution card, which is exactly what Idea 4 does when it calls this as a tool.
- **Demo artifact:** the waterfall + verbal attribution for two contrasting overruns (one
  cost-contributor-driven — a resource category dominates CV; one schedule-driven — SV off, CV small),
  each carrying the assumption-based-attribution badge and evidence-needed field, reached by clicking a
  row in Idea 1's watchlist, with the residual-ties-to-zero assertion shown.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** REFRAME (then build). The pain is real: an alert says *which* package, never
*why*, and "why" is exactly what a QS needs to act. But the original framing "split CV into quantity
vs rate" is a math error. `CV_AED = EV − AC` is the cost (efficiency) variance measured at the earned
quantity, so it has no quantity term; and on this data it cannot be split into price vs productivity
either (no hours/quantities/rates). The quantity story lives in `SV = EV − PV`. New framing: a two-lane
EVM bridge — cost/efficiency (CV, attributed by resource) and schedule/progress (SV) shown side by
side, never folded together, with the named contributor a hypothesis to confirm. And it is not a
standalone product: subtraction
default says ship it as the drill-down behind Idea 1's watchlist and a tool under the Idea 4 copilot,
not a separate app.

**What already exists (don't rebuild blindly).** The sheet hands you `CV_AED`, `SV_AED`, `EV_AED`,
`PV_AED` and the four `AC_*_AED` resource splits pre-computed. Verified exactly: `CV = EV − AC`
(diff 0), `SV = EV − PV` (diff 0), splits sum to AC. So the decomposer is not re-deriving EVM — it is
*attributing* the already-correct CV to resources using the estimate mix, and narrating it. That is
the justified thin layer on top.

**Dream-state delta.** CURRENT: QS eyeballs resource lines for hours to guess a cause -->
THIS IDEA: one click on a red flag returns a tie-out attribution card ("manpower is the dominant
cost-variance contributor at ~1.8× its norm-implied budget — confirm with hours/rates") -->
12-MONTH IDEAL: every flag on every tower auto-carries a defensible, client-ready attribution the QS
edits, not authors.

**Approaches considered & pick.**
- A) Minimal viable — CV-only, by-resource bridge that ties out, plus one-line cause. Effort S, low
  risk, reuses pre-computed CV + estimate mix. Demos the core insight.
- B) Ideal — two-lane bridge (CV cost/efficiency lane + SV schedule/progress lane) with waterfall +
  verbal attribution, wired as the click-through from Idea 1's watchlist. Effort M, low risk, reuses
  the same joins.
- **Chosen: B** because the SV lane is what keeps the attribution honest (it stops the tool from
  blaming a resource's cost when the real story is schedule/progress), and the build delta over A is
  small with AI.
  Reversible two-way door: yes (can drop the SV lane if the timebox tightens).

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Standalone product or Idea 1's drill-down? | Ship as drill-down | Subtraction default; alone it is thin, behind the watchlist it is the payoff. |
| D2 | Keep "split CV into quantity vs rate"? | Cut, reframe to two lanes | CV is the cost/efficiency variance at earned quantity (no quantity term, and not price-vs-productivity separable here); quantity/schedule is SV. Selling the split is a catchable error. |
| D3 | Add the SV / schedule lane? | Add | Prevents mis-attributing schedule/progress overruns to a resource's cost. Small delta. |
| D4 | Pre-audit the Package_Code join first? | Defer / done | Audited now: 68/68 packages match, 99.4% row coverage. Not the risk; move on. |
| D5 | Handle NOT STARTED / zero-earned rows? | Add gate | `AC ÷ earned qty` explodes at zero; gate on `Earned_Qty_Cumul > 0`. |

**Top failure modes.** 1) Selling a "quantity part of CV" — a QS spots the math is wrong and trust
dies; noticed the first time SV and the claimed quantity effect disagree. 2) The junk `AC_Cumul` block
(rows ~2078–2090) leaks in and produces nonsense packages; noticed as non-`EP-` codes on the
watchlist. 3) Zero-earned rows report absurd unit rates ("manpower 400× norm"); noticed as obviously
insane multipliers on NOT STARTED packages.

**Honest success metric.** Every diagnosed package's resource contributions + SV lane must
reconstruct its reported `CV_AED` and `SV_AED` to the AED (baseline to beat: 0 unexplained residual;
already verified exact on this data). Secondary: on ~15–20 hand-labelled overruns, the named dominant
driver matches the human call. Leakage trap: don't grade the decomposer against `Alert_Level` or any
signal derived from the same CV it is explaining — that is circular; grade against the tie-out and the
independent human label.

**Deferred to a real build (written down, not chosen).** Cross-package pattern mining (which trades
systematically blow the manpower norm across towers), and feeding confirmed rate breaches back to
correct the estimate norms for the next project.

**Verdict.** BUILD-WITH-CHANGES — reframe to the two-lane EVM bridge (cost/efficiency CV, attributed by
resource, plus a schedule/progress SV lane), attribution not root-cause diagnosis, ship it as the
drill-down behind Idea 1, keep the tie-out as the trust anchor.
