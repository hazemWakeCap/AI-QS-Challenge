# Idea 5 — Variance Root-Cause Decomposer

**One-line pitch.** Turn a red flag into a diagnosis: don't just say a package is over budget — say
*why*, split by resource into a rate/efficiency story and a separate quantity/schedule story.

**The QS pain it kills.** An alert ("BCC-STR-12 is AMBER, CV −85k") tells the QS *that* there's a
problem, not *what to do*. The QS still has to dig through resource lines to find whether it's a
labour-productivity collapse, a material price spike, or just more work than planned. This does that
dig automatically and hands over an actionable cause. It is the "why" behind Idea 1's "which."

**Approach.** An EVM variance bridge, decomposed by resource. Do NOT try to split `CV_AED` itself
into a quantity part and a rate part — that framing is wrong. `CV_AED = EV − AC` is measured *at the
earned quantity*, so it is already a pure rate/efficiency variance (it has no quantity term). The
"did more/less work than planned" effect lives in `SV_AED = EV − PV`, a different lane. Report two
honest lanes:
- **Rate/efficiency lane (this is CV).** `CV_AED = EV − AC`, decomposed by resource:
  `CV = Σ_r (EV_r − AC_r)` where `EV_r = EV_AED × norm_share_r` (the norm-implied resource share
  from `4_ESTIMATE_DATASHEET`) and `AC_r` is the actual split (`AC_Material/Manpower/Equipment/
  Subcontract_AED`). Each resource's `EV_r − AC_r` is its contribution, and they sum to `CV_AED`
  exactly. Then compare actual unit cost (`AC_r ÷ earned qty`) to the norm-implied unit rate to name
  the culprit: "manpower is 1.8× the norm rate — likely productivity, not price."
- **Quantity/schedule lane (this is SV).** `SV_AED = EV − PV`, plus `Earned_Qty_Cumul` vs
  `Planned_Qty_Period`, tells the "doing more/less work than planned" story. Show it alongside, not
  folded into CV.
- Output a plain-language cause: "Over by 85k: work is on-plan (SV ≈ 0), but **manpower cost is 1.8×
  the norm-implied rate** — productivity, not price or scope."

**Data used.** `9_HISTORICAL_DATA` for actuals and variance (`CV_AED`, `EV_AED`, `PV_AED`, `SV_AED`,
`Earned_Qty_Cumul`, the four `AC_*_AED` splits, `BAC_AED`, `Budget_Qty`, `Direct_Unit_Cost_AED`),
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
2. **Driver face-validity.** Hand-label the dominant cause on ~15–20 clearly-driven historical
   overruns and check the decomposer names the same driver. Face validity with a QS matters as much
   as any number here.

**What the QS sees.** A **variance waterfall** per flagged package: PV → SV effect → EV → each
resource's CV contribution → AC, with the biggest bar highlighted and a one-line verbal cause. The
demo is the click-through: from a red row in Idea 1's watchlist to this cause card. Also a natural
tool under the Idea 4 copilot.

**Build effort for a hackathon.** Medium. The math is arithmetic once the resource mix is aggregated
(the join is clean, see below). Most effort is the two-lane framing and the waterfall chart.

**Risks / gotchas.**
- **Do not sell "quantity vs rate split of CV."** CV is the rate variance by construction. Selling a
  quantity split of it is a math error a sharp QS will catch. Use the SV lane for quantity.
- **Junk block in the historical sheet.** Rows ~2078–2090 are a stray monthly `AC_Cumul` roll-up
  (non-`EP-` package codes, null `BCC_ID`). Filter to `Package_Code` starting `EP-` before anything.
- **Join is clean, not the crux (audited).** All 68 `EP-` packages in `9_HISTORICAL_DATA` match a
  package in `4_ESTIMATE_DATASHEET`, covering 99.4% of package-bearing rows. The earlier worry that
  the join wouldn't be clean did not hold — spend the saved time on the framing and the tie-out.
- **NOT STARTED / zero-earned rows** have no meaningful unit cost — the `AC_r ÷ earned qty` rate blows
  up. Gate on `Earned_Qty_Cumul > 0` and only diagnose live packages.

## Recommended deliverable

**A component, not an app** — a Python decomposition module + a waterfall chart, embedded where the QS
already is.
- **Form:** a decomposition module (`decompose.py`) that takes a `(BCC_ID, Period_ID)`, returns the
  two-lane bridge (CV by resource + the SV lane) with the tie-out assertion, and a **waterfall chart**
  component (Plotly) rendering PV → SV → EV → per-resource CV → AC with the one-line verbal cause. It
  is not shipped standalone: it is the **click-through from Idea 1's watchlist** (red row → cause card)
  and a **tool under Idea 4's copilot** ("explain BCC-STR-12's overrun").
- **Why this form:** subtraction default — a QS reaches a root-cause view *from* a flag, never as a
  destination. Building it as an embeddable module + chart, rather than its own dashboard, is what lets
  Ideas 1 and 4 both consume it with no rework, and keeps the tie-out logic in one tested place.
- **Not a Claude skill on its own.** The decomposition is deterministic arithmetic; the LLM's only role
  is narrating the finished cause card, which is exactly what Idea 4 does when it calls this as a tool.
- **Demo artifact:** the waterfall + verbal cause for two contrasting overruns (one rate-driven, one
  scope/SV-driven), reached by clicking a row in Idea 1's watchlist, with the residual-ties-to-zero
  assertion shown.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** REFRAME (then build). The pain is real: an alert says *which* package, never
*why*, and "why" is exactly what a QS needs to act. But the original framing "split CV into quantity
vs rate" is a math error. `CV_AED = EV − AC` is measured at the earned quantity, so it is already a
pure rate/efficiency variance with no quantity term. The quantity story lives in `SV = EV − PV`. New
framing: a two-lane EVM bridge — rate/efficiency (CV, decomposed by resource) and quantity/schedule
(SV) shown side by side, never folded together. And it is not a standalone product: subtraction
default says ship it as the drill-down behind Idea 1's watchlist and a tool under the Idea 4 copilot,
not a separate app.

**What already exists (don't rebuild blindly).** The sheet hands you `CV_AED`, `SV_AED`, `EV_AED`,
`PV_AED` and the four `AC_*_AED` resource splits pre-computed. Verified exactly: `CV = EV − AC`
(diff 0), `SV = EV − PV` (diff 0), splits sum to AC. So the decomposer is not re-deriving EVM — it is
*attributing* the already-correct CV to resources using the estimate mix, and narrating it. That is
the justified thin layer on top.

**Dream-state delta.** CURRENT: QS eyeballs resource lines for hours to guess a cause -->
THIS IDEA: one click on a red flag returns a tie-out cause card ("manpower 1.8× norm rate") -->
12-MONTH IDEAL: every flag on every tower auto-carries a defensible, client-ready cause the QS edits,
not authors.

**Approaches considered & pick.**
- A) Minimal viable — CV-only, by-resource bridge that ties out, plus one-line cause. Effort S, low
  risk, reuses pre-computed CV + estimate mix. Demos the core insight.
- B) Ideal — two-lane bridge (CV rate lane + SV quantity lane) with waterfall + verbal cause, wired
  as the click-through from Idea 1's watchlist. Effort M, low risk, reuses the same joins.
- **Chosen: B** because the SV lane is what keeps the diagnosis honest (it stops the tool from
  blaming rate when the real story is scope/schedule), and the build delta over A is small with AI.
  Reversible two-way door: yes (can drop the SV lane if the timebox tightens).

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Standalone product or Idea 1's drill-down? | Ship as drill-down | Subtraction default; alone it is thin, behind the watchlist it is the payoff. |
| D2 | Keep "split CV into quantity vs rate"? | Cut, reframe to two lanes | CV is already the rate variance; quantity is SV. Selling the split is a catchable error. |
| D3 | Add the SV / quantity lane? | Add | Prevents mis-attributing scope/schedule overruns to rate. Small delta. |
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

**Verdict.** BUILD-WITH-CHANGES — reframe to the two-lane EVM bridge, ship it as the drill-down behind
Idea 1, keep the tie-out as the trust anchor.
