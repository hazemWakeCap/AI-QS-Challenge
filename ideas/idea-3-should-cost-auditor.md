# Idea 3 — Bottom-Up "Should-Cost" Estimate Auditor

**One-line pitch.** Rebuild every package's cost from first principles (norms + rates) and flag the
estimates that are optimistic *on day zero* — before a single shift is worked.

**The QS pain it kills.** Most cost trouble isn't bad execution; it's a budget that was never
realistic. The other ideas watch work drift *after* it starts. This one catches the trouble baked
into the estimate itself, when it's still cheap to fix (re-price, add contingency, challenge a norm).

**Approach.** A deterministic engine, not an ML model. Read the next paragraph first: the obvious
version of this idea is a tautology, so the real work is scoring optimism *without touching a
package's own actuals*.

*The trap (proven on the data).* The dataset reconciles end to end, so should-cost from norms times
rates equals the BOQ direct cost by construction. That comparison only re-derives the explicit
`Margin %` and `Cont %` columns... zero signal. Worse, sheet `9_HISTORICAL_DATA` is Tower X's own
budget-vs-actual panel (all 68 estimate packages map 1:1 to its `Package_Code`s), and `CPI = EV/AC`
held on 1166/1166 rows. Since estimate unit cost is EV/qty and actual unit cost is AC/qty,
"estimate priced below its actual unit cost" is *identically* `CPI < 1`. So flagging a package
against its own actual, then "back-testing" that it later overran, proves nothing.

*The engine that survives.* Build a leakage-free **optimism prior** from the estimate side only,
using zero actuals:
- Norm quantity math: manpower/equipment qty = `BOQ Quantity × (gang or equipment count ÷ Output
  Norm)`; materials/subcontract scale with `BOQ Quantity`. Times `Unit Rate (AED)`. **This is the
  exact correction in `data/README.md`** ... an earlier draft dropped the Output-Norm divisor and
  overstated labour. Getting it right is what lets the reconciliation check pass.
- Score each package's optimism from signals a QS could see on day zero: `Output Norm` in the top
  percentile of its sub-trade (aggressive productivity assumption), `Unit Rate (AED)` at the bottom
  of the plausible band for the resource, risky norm `Notes` adjustments (e.g. "−30% for confined
  spaces") applied to inflate output, and thin or zero `Cont %`. None of these read an actual.
- Add a **leave-one-out peer benchmark**: for package P, benchmark its expected unit cost from the
  realized actuals of *other* packages in the same discipline/sub-trade, never P's own. Flag P if
  its estimate sits below that peer benchmark. This breaks the identity above and is the only
  history-based flag that is not circular.

**Data used.** Sheets `1_BOQ` (`Quantity`, `TOTAL Amount`, `Norm Ref`), `2_ESTIMATE_NORMS`
(`Output Norm`, `Gang Size`, gang/equipment counts, `Notes`), `3_BOQ_MAPPING` (`Estimate Package`,
`Op Code`), `4_ESTIMATE_DATASHEET` (`Unit Rate (AED)`, `Resource Type`, `Total Resource Qty`).
Joins: `Norm Code` links norms/mapping/datasheet; `BOQ Sec`+`Item` links to the BOQ; `Estimate
Package` = `Package_Code` (verified 68/68). Sheet `9_HISTORICAL_DATA` is used only for the
leave-one-out peer benchmark and for out-of-sample validation, never inside a package's own flag.

**How you'd judge it's good.** Precision/recall of the leakage-free optimism flag against subsequent
`CPI < 1`, computed leave-one-out so no package scores itself. The number that matters: it must
**beat the dumb baseline** "budgeted unit cost in the bottom quartile of its discipline" by a stated
margin, otherwise the four-sheet plumbing bought nothing over one column. Reject the naive
"flagged-then-overran" claim outright: that is `CPI < 1` by identity, not evidence. Separately, the
reconciliation tie-out (unflagged should-cost equals BOQ direct cost end to end) is the engine's
correctness proof, not its signal.

**What the QS sees.** An **estimate-risk heatmap** by package/discipline: red = optimism-flagged
before any work starts, with the resource line driving it ("labour rate 22% under the sub-trade
benchmark; Output Norm in the top 10% for CIV-DEMO"). The point is pre-execution: a package the QS
can re-price, add contingency to, or challenge the norm on, while it is still free to fix.

**Build effort for a hackathon.** Medium. No modelling, but real data-plumbing: joining four sheets
cleanly, getting the norm-quantity divisor right, and keeping the flag strictly estimate-side. The
reconciliation tie-out doubles as the correctness proof.

**Risks / gotchas.** The whole idea dies if the flag touches a package's own actual (tautology, see
above), so guard that boundary hard. Getting the Output-Norm divisor right is load-bearing; validate
against the README note or the reconciliation fails. Norm `Notes` adjustments must be applied. Thin
sub-trades (1-2 peer packages) make the leave-one-out benchmark noisy, so fall back to the pure
estimate-side prior there. This is the most *differentiated* idea, the only one mining the estimate
sheets, but its headline mechanism has to be rebuilt to be honest.

## Recommended deliverable

**A deterministic Python audit engine + an estimate-risk heatmap** (report-first, dashboard-optional).
- **Form:** the core is a batch Python engine (`should_cost.py`) that joins sheets 1–4, applies the
  Output-Norm-corrected quantity math, computes the leakage-free optimism score and the leave-one-out
  peer benchmark, and emits a ranked table + the resource line driving each flag. The view is an
  estimate-risk **heatmap** by package/discipline — a Streamlit page, or, since this is run once per
  estimate rather than watched live, an exported HTML/PDF report works equally well.
- **Why this form:** this is a pre-execution, run-at-award artifact, not a live monitor, so a generated
  report the QS attaches to an estimate review is as useful as an app. What must be first-class is the
  **reconciliation test suite** (unflagged should-cost ties to BOQ direct cost end to end) — that
  pytest file *is* the credibility of the engine and ships alongside it.
- **Not a Claude skill.** Pure deterministic computation with no language step. An LLM would only
  obscure a calculation that must be auditable to the AED.
- **Demo artifact:** the heatmap + the passing reconciliation test + the precision/recall-vs-
  bottom-quartile-baseline table.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** Reframe, do not kill. The problem (catch a bad budget before execution) is
real and this is the only idea that attacks it pre-execution, so the angle is worth keeping. But two
mechanisms in the original spec are tautologies confirmed on the data: (1) should-cost equals BOQ
direct cost because the model reconciles end to end, so that comparison only re-derives margin and
contingency; (2) the "flag under-priced, watch it overrun" back-test is `CPI < 1` by identity
(`CPI = EV/AC` on 1166/1166 rows, and estimate-vs-own-actual unit cost IS EV/AC). New framing: an
estimate-side **optimism prior** that never reads a package's own actual, validated leave-one-out
against later `CPI < 1`, and required to beat a bottom-quartile baseline.

**What already exists (don't rebuild blindly).** The reconciled build-up (`4_ESTIMATE_DATASHEET`
rolls to `1_BOQ` totals) and the pre-computed EVM in `9_HISTORICAL_DATA` (`CPI`, `EAC`,
`Alert_Level`). The engine leans on the reconciliation only as a correctness tie-out, not a signal,
and on the actuals only for a leave-one-out peer benchmark. Rebuilding norms from scratch is
justified for one reason: resource-line *attribution* (labour vs material vs equipment), which the
EVM columns do not give you.

**Dream-state delta.** CURRENT: QS finds the budget was unrealistic months in, after the money is
gone. THIS IDEA: a day-zero heatmap of packages priced below what the trade actually costs, with the
resource driving it. 12-MONTH IDEAL: every estimate ships with an auto-generated optimism score and
a suggested re-price/contingency before award.

**Approaches considered & pick.**
- A) Estimate-side optimism prior only (Output-Norm percentile, unit-rate band, risky Notes, thin
  contingency) validated vs `CPI < 1`. Effort M, low leakage risk, reuses sheets 2/4, no history in
  the flag.
- B) A plus a leave-one-out peer-actual benchmark and resource-line attribution from the sheet-9
  `AC_*_AED` split. Effort M/L, higher wow, reuses sheet 9 safely.
- **Chosen: B**, because attribution ("labour 22% under benchmark") is the differentiated demo
  moment and the leave-one-out benchmark is the only non-circular way to use the actuals. Reversible
  two-way door: yes, the flag/metric definition is easy to swap.

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Keep should-cost vs BOQ as a signal? | Cut | Reconciled, so it only re-derives margin+contingency. Keep it only as the tie-out proof. |
| D2 | Keep the "flagged then overran" back-test? | Cut | It is `CPI < 1` by identity; replace with leave-one-out flag that never reads the package's own actual. |
| D3 | Add resource-line attribution from sheet-9 `AC_*_AED`? | Add | Cheap, differentiated, drives the demo line the QS remembers. |
| D4 | Add a bottom-quartile baseline to beat? | Add | Proves the four-sheet plumbing earns its keep over one column. |
| D5 | Apply norm `Notes` adjustments (e.g. −30% confined)? | Add | Load-bearing and itself a prime optimism signal. |

**Top failure modes.** 1) Tautology leak: any actual of a package feeding its own flag makes the
back-test perfect... a suspiciously clean ~100% precision/recall is the tell. 2) Output-Norm divisor
dropped: labour overstated, everything flags "over-priced" (wrong direction); the QS notices the
reconciliation tie-out fails. 3) Thin sub-trades: 1-2 peer packages make the leave-one-out benchmark
noise; the QS notices single-package disciplines flipping red on re-run.

**Honest success metric.** Precision/recall of the leakage-free optimism flag vs subsequent
`CPI < 1`, leave-one-out, that beats the baseline "budgeted unit cost in the bottom quartile of its
discipline" by a stated margin (target +10pp precision at equal recall). Leakage trap to avoid: never
let a package's own actual or CPI enter its flag; the naive estimate-vs-own-actual comparison is CPI
by identity and is not evidence.

**Deferred to a real build (written down, not chosen).** A true cross-project historical rate library
for external benchmarks; Monte-Carlo on norm/rate uncertainty to output a priced confidence band;
auto-suggested re-price and contingency amounts per flagged package.

**Verdict.** BUILD-WITH-CHANGES. The pre-execution angle is genuinely differentiated, but the
headline mechanism and back-test are both tautologies as written and must be rebuilt estimate-side
and leave-one-out to be honest.
