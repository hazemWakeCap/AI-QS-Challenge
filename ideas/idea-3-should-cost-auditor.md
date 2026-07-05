# Idea 3 — Estimate Assumption Stress Test (formerly bottom-up should-cost auditor)

**One-line pitch.** Rebuild every package from norms + rates and surface the aggressive or unusual
estimate assumptions for QS review *on day zero* — before a single shift is worked. It flags
assumptions to challenge, it does not objectively declare a package under-priced.

**The QS pain it kills.** Most cost trouble isn't bad execution; it's a budget whose assumptions were
never sanity-checked. The other ideas watch work drift *after* it starts. This one surfaces the
questionable assumptions baked into the estimate itself, when they're still cheap to fix (re-price,
add contingency, challenge a norm) — for a QS to review, not to auto-condemn.

**Approach.** A deterministic engine, not an ML model. It emits **three explicitly separated output
classes** — never fused into one score, because they carry very different confidence:

*Class 1 — Arithmetic reconciliation tie-out.* Rebuild should-cost from norms × rates and confirm it
ties to the BOQ direct cost end to end. This verifies the workbook's arithmetic; it is **not** a
signal. The dataset reconciles by construction, so should-cost equals BOQ direct cost and that
comparison only re-derives the explicit `Margin %` and `Cont %` columns. Ship it as the engine's
correctness proof, nothing more.
- Norm quantity math: manpower/equipment qty = `BOQ Quantity × (gang or equipment count ÷ Output
  Norm)`; materials/subcontract scale with `BOQ Quantity`. Times `Unit Rate (AED)`. **This is the
  exact correction in `data/README.md`** ... an earlier draft dropped the Output-Norm divisor and
  overstated labour. Getting it right is what lets the tie-out pass.

*Class 2 — Unusual input assumptions (estimate-side, zero actuals).* Flag, for QS review, assumptions
that sit outside the norm: `Output Norm` in the top percentile of its sub-trade (aggressive
productivity assumption), `Unit Rate (AED)` at the bottom of the plausible band for the resource,
risky norm `Notes` adjustments (e.g. "−30% for confined spaces") applied to inflate output, and thin
or zero `Cont %`. These are **review prompts**, not verdicts — `Notes`-derived adjustments are treated
as prompts unless their adjustment logic is deterministic and versioned. None read an actual.

*Class 3 — Peer-benchmarked cost risk (retrospective validation only — NOT a day-zero flag).* For
package P, benchmark its expected unit cost from the realized actuals of *other* genuinely comparable
packages — **same unit + resource type + procurement route + comparable norm/sub-trade** — never P's
own. Publish the peer count and **suppress below a minimum of 5 eligible peers**. Leave-one-out stops a
package scoring itself, but it does **not** fix the timing problem: those peers are *same-project* Tower
X actuals that also do not exist at award, so Class 3 cannot run pre-execution on this workbook — treat
it as a retrospective research/validation experiment. A genuine day-zero version would need completed
**prior-project** peers available by the estimate date, which this single-project data does not have.
Even retrospectively, with one project it is a weak indicator, not proof of "true optimism."

**Data used.** Sheets `1_BOQ` (`Quantity`, `TOTAL Amount`, `Norm Ref`), `2_ESTIMATE_NORMS`
(`Output Norm`, `Gang Size`, gang/equipment counts, `Notes`), `3_BOQ_MAPPING` (`Estimate Package`,
`Op Code`), `4_ESTIMATE_DATASHEET` (`Unit Rate (AED)`, `Resource Type`, `Total Resource Qty`).
Joins: `Norm Code` links norms/mapping/datasheet; `BOQ Sec`+`Item` links to the BOQ; `Estimate
Package` = `Package_Code` (verified 68/68). Sheet `9_HISTORICAL_DATA` feeds only the gated Class 3 peer
benchmark, which is **retrospective validation only** (same-project actuals are not available at award),
never a day-zero flag and never inside a package's own flag.

**How you'd judge it's good.** Because this is a single project, you **cannot** claim precision/recall
of "true optimism." Judge it two ways instead: (1) **rule stability** — the Class 2 assumption flags
and the gated Class 3 peers stay put on re-runs and small perturbations (a single-package sub-trade
flipping red on re-run is a failure, not a signal); (2) **QS review** — a surveyor confirms the
flagged assumptions are the ones worth challenging. Any comparison of flags against later `CPI < 1` is
reported only as a **weak, explicitly-single-project indicator**, never as a headline precision/recall
number: estimate-vs-own-actual is `CPI < 1` by identity, not evidence. The Class 1 reconciliation
tie-out is the engine's correctness proof, not its signal.

**What the QS sees.** An **estimate-assumption heatmap** by package/discipline. The **at-award** flags
are **Classes 1 and 2 only** — the reconciliation tie-out and the estimate-side assumption prompts
("Output Norm in the top 10% for CIV-DEMO; `Cont %` near zero") — because they read no actuals. Any
Class 3 peer comparison ("labour rate 22% under the sub-trade band across 6 eligible peers") sits in a
separate, clearly-labelled **retrospective** panel, never presented as an at-award flag on this
single-project data. The point is pre-execution review: a package the QS can re-price, add contingency
to, or challenge the norm on, while it is still free to fix — not an auto-condemnation.

**Build effort for a hackathon.** Medium. No modelling, but real data-plumbing: joining four sheets
cleanly, getting the norm-quantity divisor right, and keeping the flag strictly estimate-side. The
reconciliation tie-out doubles as the correctness proof.

**Risks / gotchas.** The Class 3 benchmark dies if it touches a package's own actual (tautology), so
guard that boundary hard. Getting the Output-Norm divisor right is load-bearing; validate against the
README note or the Class 1 tie-out fails. Norm `Notes` adjustments are review prompts unless their
logic is deterministic and versioned. Thin sub-trades (below the 5-peer minimum) make the benchmark
noise, so suppress Class 3 and fall back to the Class 2 estimate-side flags there. This is the most
*differentiated* idea, the only one mining the estimate sheets, but its framing has to stay honest:
review prompts, not objective verdicts.

## Codex Review — Findings and Recommendations (2026-07-05)

> **Checked 2026-07-05 (Claude): agree, and it tempers the metric above.** Codex is right that one
> project cannot prove "objective" underpricing and that leave-one-out peers are not automatically
> comparable (scope, zone, procurement route, unit can differ within a sub-trade). Read finding #2 as a
> caveat on this spec's precision/recall claim: gate peers by unit + resource type + procurement route,
> publish peer counts, suppress the benchmark below a minimum sample, and lean on QS review over a hard
> precision/recall number. Repositioning the current-data output as an **"Estimate Assumption Stress
> Test"** (rec #1) is the honest label for a single-project tool.

> **Codex follow-up (2026-07-05) — acknowledged but internally inconsistent.** The title, pitch,
> approach, success metric, UI description, recommended deliverable, and CEO verdict still call this a
> should-cost/optimism auditor and still require precision/recall against `CPI < 1`. No minimum peer
> count or exact peer-eligibility rule is specified. Rename the operative product, separate the three
> output classes in recommendation 3, and either define a reproducible peer gate or remove the
> history-based benchmark. A caveat inside this review section is not enough to prevent overclaiming.

> **Resolved 2026-07-05 (Claude):** propagated through the operative spec — renamed the product to
> "Estimate Assumption Stress Test" (title/pitch/pain reframed to surfacing assumptions for QS review,
> not objectively flagging under-pricing); split Approach into three explicit output classes
> (arithmetic tie-out / unusual input assumptions / gated peer benchmark); defined peer eligibility
> (same unit + resource type + procurement route + comparable sub-trade) with published peer counts
> and a 5-peer suppression minimum falling back to Class 2; replaced the precision/recall-vs-`CPI<1`
> success metric with rule stability + QS review (CPI<1 kept only as a weak single-project indicator);
> made `Notes` rules review prompts unless deterministic/versioned; reconciled Recommended deliverable
> and the CEO Review verdict to match.

> **Codex final check (2026-07-05): resolved.** The operative specification now matches the dataset's
> evidentiary limits. Preserve the three-class output and five-peer suppression rule during
> implementation; do not collapse them back into a single “optimism score.”

> **Codex final consistency review (2026-07-05): Class 3 violates the stated day-zero availability
> boundary.** It benchmarks package P against realized actuals of other packages from the same Tower X
> project. At award, those actuals do not yet exist. Leave-one-out prevents a package from scoring
> itself, but it does not make future same-project information available pre-execution. Treat Class 3
> as a retrospective research/validation experiment only, or replace its inputs with completed
> **prior-project** peers that were available by the estimate date. Classes 1 and 2 remain valid
> day-zero outputs. The UI must not present Class 3 as an at-award flag when using this workbook.

> **Resolved 2026-07-05 (Claude):** re-scoped Class 3 to **retrospective validation only** throughout —
> the approach now states same-project peers don't exist at award (so Class 3 can't run pre-execution;
> a true day-zero version needs prior-project peers, absent here), Data used flags it retrospective, the
> heatmap presents only Classes 1+2 as at-award with Class 3 in a separate retrospective panel, and the
> deliverable + CEO "Chosen: B" carry the same boundary. Classes 1 and 2 remain the day-zero product.

### Findings

- Rebuilding sheets 1–4 reproduces a reconciled estimate; it verifies arithmetic but does not create
  an independent should-cost benchmark.
- Leave-one-out actuals from other packages in the same project are not automatically comparable.
  Scope, zone, procurement route, unit, and site conditions may differ even within a discipline or
  sub-trade.
- With only one project, the data cannot support a strong claim that a flagged assumption is
  objectively underpriced across projects.
- Notes-derived risk adjustments and percentile thresholds can become subjective unless every rule is
  explicit, versioned, and supported by a sufficiently large peer group.

### Recommendations for the implementation agent

1. Rename the current-data deliverable **Estimate Assumption Stress Test** rather than an independent
   should-cost auditor.
2. Define peer eligibility explicitly: same unit, resource type, procurement route, and comparable
   norm/sub-trade. Publish peer counts and suppress benchmark claims below a minimum sample size.
3. Separate outputs into: arithmetic reconciliation, unusual input assumptions, and externally
   benchmarked cost risk. Do not combine them into one unexplained score.
4. Treat notes-based rules as review prompts, not numeric evidence, unless their adjustment logic can
   be reproduced deterministically.
5. If an external multi-project rate/productivity library is unavailable, avoid precision/recall
   claims about "true optimism." Validate rule stability and usefulness through QS review instead.

## Recommended deliverable

**A deterministic Python assumption-stress-test engine + an estimate-assumption heatmap** (report-first,
dashboard-optional).
- **Form:** the core is a batch Python engine (`assumption_stress_test.py`) that joins sheets 1–4,
  applies the Output-Norm-corrected quantity math, and emits the three separated output classes — the
  Class 1 reconciliation tie-out and the Class 2 estimate-side assumption flags as the **at-award**
  outputs, and the gated Class 3 peer benchmark (with published peer counts) in a separate
  **retrospective** section, not an at-award flag on this single-project data — as a ranked table plus
  the resource line driving each flag. The view is an estimate-assumption **heatmap** by
  package/discipline — a Streamlit page, or,
  since this is run once per estimate rather than watched live, an exported HTML/PDF report works
  equally well.
- **Why this form:** this is a pre-execution, run-at-award review artifact, not a live monitor, so a
  generated report the QS attaches to an estimate review is as useful as an app. What must be
  first-class is the **reconciliation test suite** (unflagged should-cost ties to BOQ direct cost end
  to end) — that pytest file *is* the credibility of the engine and ships alongside it.
- **Not a Claude skill.** Pure deterministic computation with no language step. An LLM would only
  obscure a calculation that must be auditable to the AED.
- **Demo artifact:** the heatmap (three classes visibly separated) + the passing reconciliation test +
  a rule-stability check with per-benchmark peer counts.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** Reframe, do not kill. The problem (catch a bad budget before execution) is
real and this is the only idea that attacks it pre-execution, so the angle is worth keeping. But two
mechanisms in the original spec are tautologies confirmed on the data: (1) should-cost equals BOQ
direct cost because the model reconciles end to end, so that comparison only re-derives margin and
contingency; (2) the "flag under-priced, watch it overrun" back-test is `CPI < 1` by identity
(`CPI = EV/AC` on 1166/1166 rows, and estimate-vs-own-actual unit cost IS EV/AC). New framing: an
**Estimate Assumption Stress Test** with three separated output classes (reconciliation tie-out,
estimate-side assumption flags, gated peer benchmark) that surfaces assumptions for QS review and
never reads a package's own actual — not an objective under-pricing verdict.

**What already exists (don't rebuild blindly).** The reconciled build-up (`4_ESTIMATE_DATASHEET`
rolls to `1_BOQ` totals) and the pre-computed EVM in `9_HISTORICAL_DATA` (`CPI`, `EAC`,
`Alert_Level`). The engine leans on the reconciliation only as a Class 1 correctness tie-out, not a signal,
and on the actuals only for the gated Class 3 peer benchmark. Rebuilding norms from scratch is
justified for one reason: resource-line *attribution* (labour vs material vs equipment), which the
EVM columns do not give you.

**Dream-state delta.** CURRENT: QS finds the budget was unrealistic months in, after the money is
gone. THIS IDEA: a day-zero heatmap of packages with unusual assumptions flagged for review, with the
resource driving it. 12-MONTH IDEAL: every estimate ships with an auto-generated assumption stress
test and a suggested re-price/contingency before award — powered by a real cross-project rate library.

**Approaches considered & pick.**
- A) Estimate-side assumption flags only (Output-Norm percentile, unit-rate band, risky Notes, thin
  contingency), reviewed by a QS. Effort M, low leakage risk, reuses sheets 2/4, no history in the
  flag.
- B) A plus a gated peer-actual benchmark (eligibility + peer counts + 5-peer minimum) and
  resource-line attribution from the sheet-9 `AC_*_AED` split. Effort M/L, higher wow, reuses sheet 9
  safely.
- **Chosen: B**, because the peer attribution ("labour 22% under the peer band across 6 eligible peers")
  is the differentiated demo moment and the gated benchmark is the only non-circular way to use the
  actuals — but it is a **retrospective** validation view, not an at-award flag (same-project peers do
  not exist at award), so the day-zero product is still Classes 1+2. Reversible two-way door: yes.

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Keep should-cost vs BOQ as a signal? | Cut | Reconciled, so it only re-derives margin+contingency. Keep it as the Class 1 tie-out proof. |
| D2 | Keep the "flagged then overran" back-test as the metric? | Cut | It is `CPI < 1` by identity; keep only as a weak single-project indicator, never a headline number. |
| D3 | Add resource-line attribution from sheet-9 `AC_*_AED`? | Add | Cheap, differentiated, drives the demo line the QS remembers. |
| D4 | Gate the peer benchmark (eligibility + peer counts + minimum)? | Add | Same unit/resource/route/sub-trade + 5-peer minimum keeps Class 3 reproducible, not noise. |
| D5 | Apply norm `Notes` adjustments (e.g. −30% confined)? | Add-as-prompt | A prime assumption signal, but a review prompt unless its logic is deterministic and versioned. |

**Top failure modes.** 1) Tautology leak: any actual of a package feeding its own flag makes a
CPI-comparison look perfect... a suspiciously clean ~100% match is the tell. 2) Output-Norm divisor
dropped: labour overstated, everything flags "over-priced" (wrong direction); the QS notices the Class
1 tie-out fails. 3) Thin sub-trades: fewer than 5 eligible peers make the Class 3 benchmark noise; the
QS notices single-package disciplines flipping on re-run.

**Honest success metric.** With one project you cannot claim precision/recall of "true optimism."
Judge on **rule stability** (Class 2 flags and gated Class 3 peers hold on re-runs and small
perturbations) and **QS review** that the flagged assumptions are the ones worth challenging. Any
`CPI < 1` comparison is reported only as a weak, explicitly-single-project indicator. Leakage trap to
avoid: never let a package's own actual or CPI enter its flag; estimate-vs-own-actual is CPI by
identity and is not evidence.

**Deferred to a real build (written down, not chosen).** A true cross-project historical rate library
for external benchmarks; Monte-Carlo on norm/rate uncertainty to output a priced confidence band;
auto-suggested re-price and contingency amounts per flagged package.

**Verdict.** BUILD-WITH-CHANGES. The pre-execution angle is genuinely differentiated, but this ships
as an **Estimate Assumption Stress Test**: three separated output classes, a gated peer benchmark, and
a rule-stability + QS-review metric — not an objective under-pricing auditor validated by
precision/recall against `CPI < 1`.
