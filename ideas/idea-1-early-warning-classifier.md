# Idea 1 — Early-Warning Drift Classifier

**One-line pitch.** A model that flags a cost centre a month *before* it turns AMBER, so the QS
acts while the money is still unspent.

**The QS pain it kills.** By the time a package shows up red at month-end close, the invoices are
paid and the overrun is locked in. The QS wants the flag *one period early*, on the packages that are
about to slip — not a post-mortem.

**Approach.** Predict the *transition*, not the state. For each `(BCC_ID, Period_ID)` row, build a
feature vector from the *current* period and predict whether the centre is **GREEN now and AMBER
next period** (the GREEN→AMBER flip). That framing, not "will it be AMBER," is the whole game:
AMBER is sticky, so "predict next-period AMBER" is mostly solved by "it's already AMBER" and looks
great while helping the QS with nothing.
- Target: `Alert_Level(t+1) == AMBER` **restricted to rows where `Alert_Level(t) == GREEN`**. The
  event we sell is the newly-lit flag.
- Features: `CPI`, `Rolling_3M_CPI`, `SPI`, the **gap = `Pct_Budget_Consumed − Actual_Pct_Complete`**
  (spending ahead of progress is the classic tell), the *change* in that gap and in CPI over the last
  1–2 periods (trend, not level, is what leads), `Variance_Pct`, `EAC_vs_BAC_Ratio`, and the four
  resource-split shares (`AC_Material/Manpower/Equipment/Subcontract` ÷ `AC_AED_Period`), plus
  `Discipline`/`Package_Code` as categoricals.
- Model: **the primary deliverable is the transparent rule**, not the tree. Fit a 1–2 variable rule
  ("gap > X and gap rising" → watch) and read its precision/recall first. Only then fit
  gradient-boosted trees (XGBoost/LightGBM) and keep them **only if they beat the rule on the
  transition event by a margin worth the opacity.** Tree feature importances are the fallback story,
  not the headline.

**Leakage warning built into the framing.** `Alert_Level` is a deterministic threshold on this
period's own CPI — **verified against the workbook: AMBER ≡ `CPI < 0.95` on all 1,163 live GREEN/AMBER
rows (0 mismatches), and `Risk_Flag = Medium` is identical to AMBER.** So the honest modeled event is
**"next period's `CPI` breaches 0.95"**; AMBER is just its business-facing label. Do **not** feed the
model anything that encodes next period's CPI/label, and frame the demo as *forecasting the CPI
crossing*, not discovering a hidden risk signal. Because the boundary is CPI-native, the strongest
baselines are too: current CPI, distance to the 0.95 boundary, one-period CPI change, and rolling-CPI
trend, alongside the budget-consumed/progress gap.

**Data used.** `9_HISTORICAL_DATA` only. Join the current row to its successor on
`BCC_ID` + consecutive `Period_ID` to create the (features_t → label_t+1) training pairs. Real
column names live on **row 5** of the sheet (rows 1–4 are banners — load with header=row 5).

**How you'd judge it's good.** Use **rolling-origin validation** across several target periods, not a
single final-period test: repeatedly train on periods up to `t` and score the GREEN→AMBER flip at
`t+1`, walking `t` forward, so the result isn't hostage to one lucky/unlucky test period. Never
shuffle rows (shuffling leaks the future of the same centre into training). There is a **set of
transparent baselines to beat, and the honest ones are hard:**
1. *Trivial baseline* — "flag when already AMBER." Beating this is meaningless; it catches zero
   transitions by construction. Use it only to show why the transition framing is the real task.
2. *Real baselines (CPI-native)* — because the boundary is a CPI threshold, the honest comparators are
   **current CPI, distance to the 0.95 boundary, one-period CPI change, and rolling-CPI trend**,
   **alongside** the transparent gap rule on `gap = Pct_Budget_Consumed − Actual_Pct_Complete` (flag
   GREEN centres where gap > X). The gap rule is one of several, not "the" baseline. If a tree can't
   clear this whole set on the GREEN→AMBER event, ship the best transparent rule and say so.

Report, on GREEN→AMBER transitions only: **raw TP/FP/FN counts, precision, and recall** of the
newly-lit flag, plus **precision@top-5 and precision@top-10** (the QS acts on the top of the
watchlist) and **false alerts per reporting cycle** (what the QS actually pays for in wasted chases).
Precision matters most; a QS ignores a watchlist that cries wolf. Treat **lead time gained** as a
descriptive secondary metric, not the headline — with the task fixed at predicting `t+1` from `t` it
is nearly mechanical. Judge on recall of the rare transition event, never on overall accuracy (a
"GREEN forever" predictor scores >90% and is useless). Keep the transparent rule as the spine: a
challenger model earns its place only if it **beats held-out precision at equal recall by a
predeclared margin**.

**What the QS sees.** A ranked weekly/monthly **watchlist**: "These 6 BCCs are likely to go AMBER
next period," each with its top 2–3 driving features ("spending 18% ahead of progress; CPI trending
down 3 months"). One screen, sorted by risk.

**Build effort for a hackathon.** Low–medium. Labels and most features already exist in the sheet, so
it's mostly feature engineering + a standard classifier + a lead-time metric. A notebook plus a small
Streamlit table is a credible demo in the timebox.

**Risks / gotchas.** Only two live severity classes (GREEN, AMBER) and **no RED** — frame honestly as
GREEN→AMBER early detection, not multi-class severity. `Alert_Level` is a verified CPI threshold
(AMBER ≡ `CPI < 0.95`), so the whole exercise is really *forecasting a CPI crossing*; own that in the
demo rather than dressing it as pattern discovery. Thin positives: with 173 centres × 12 periods and
only GREEN→AMBER flips counting, the event set is small, so a single lucky/unlucky test period can
swing the numbers. Report the confusion counts, not just rates. Class imbalance is severe (most rows
GREEN or NOT STARTED) — use class weights, judge on transition recall. Drop `NOT STARTED` and
zero-earned-value rows before building pairs so they don't dominate. Load `9_HISTORICAL_DATA` with
headers on **row 5** (rows 1–4 are banners: `header=4` / `skiprows=4`).

## Codex Review — Findings and Recommendations (2026-07-05)

> **Checked 2026-07-05 (Claude): all three empirical claims reproduced against the workbook** — AMBER ≡
> `CPI < 0.95` (0 / 1,163 live-row mismatch), `Risk_Flag = Medium` ≡ AMBER (0 mismatch), 117 GREEN→AMBER
> transitions across 74 centres. Confirmed and folded into the spec above: the framing now states the
> verified CPI-0.95 boundary and adopts the CPI-native baselines.

> **Codex follow-up (2026-07-05) — partially handled.** The framing and empirical claims are now
> correct, but the operative **How you'd judge it's good** and **Recommended deliverable** sections
> still define the gap rule as the sole real baseline, emphasize lead time, and omit rolling-origin
> folds, precision@5/@10, false alerts per cycle, and the predeclared model-improvement margin. Update
> those sections before implementation; otherwise an agent following the main spec will not implement
> recommendations 2–5 below.

> **Resolved 2026-07-05 (Claude):** propagated through the operative spec — **How you'd judge it's
> good** now uses rolling-origin validation across several periods, a CPI-native baseline set (current
> CPI, distance to 0.95, one-period CPI change, rolling-CPI trend) alongside the demoted gap rule,
> reports raw TP/FP/FN + precision/recall + precision@5/@10 + false alerts per cycle with lead time
> demoted to secondary, and requires a challenger to beat held-out precision at equal recall by a
> predeclared margin; **Recommended deliverable** now exposes precision@k and false-alerts-per-cycle
> from `score.py` and back-tests on rolling-origin folds. CEO-review success metric reconciled to match.

> **Codex final check (2026-07-05): one factual correction remains.** Replace `~174 centres` in the
> risks paragraph with **173 centres**. The workbook contains 173 non-null `BCC_ID` values × 12 periods
> = 2,076 panel rows. The reconciled target, validation, metrics, and deliverable are otherwise ready.

> **Resolved 2026-07-05 (Claude):** corrected `~174 centres` → **173 centres** in the risks paragraph.
> Verified against the workbook: 173 non-null `BCC_ID` × 12 periods = 2,076 panel rows.

### Findings

- In the workbook, `Alert_Level = AMBER` is exactly equivalent to `CPI < 0.95` on all 1,163 live
  GREEN/AMBER rows. `Risk_Flag = Medium` is also identical to AMBER. The target is therefore not an
  independently observed risk outcome; it is a named CPI threshold.
- The panel contains 117 GREEN→AMBER transitions across 74 cost centres. This is usable for an
  exploratory transition model, but thin for claiming robust generalisation—especially with only 12
  reporting periods.
- "Lead time gained" is nearly mechanical when the task is fixed at predicting `t+1` from `t`. It
  does not, by itself, show that the watchlist is operationally useful.
- The strongest honest baselines are likely current CPI, distance from the 0.95 boundary, CPI trend,
  and rolling CPI—not only the budget-consumed/progress gap.

### Recommendations for the implementation agent

1. Rename the modeled event internally to **next-period CPI threshold breach (`CPI < 0.95`)** and
   explain that AMBER is its business-facing label.
2. Establish four transparent baselines before fitting ML: current CPI, distance to 0.95, one-period
   CPI change, and rolling-CPI trend. Keep the gap rule as an additional comparator.
3. Use rolling-origin validation across several target periods; do not rely on one final-period test
   or shuffled rows.
4. Report raw TP/FP/FN counts, precision and recall, **precision at top 5/top 10**, and false alerts
   per reporting cycle. Treat lead time as descriptive, not the primary success metric.
5. Keep the transparent rule unless a challenger improves held-out precision at the same recall by a
   meaningful, predeclared margin. Do not claim cross-project generalisation from this workbook.

## Recommended deliverable

**Software dashboard** — a ranked watchlist screen, backed by a Python scoring module.
- **Form:** a Streamlit (or Plotly Dash) single screen: the sortable watchlist of GREEN centres likely
  to flip AMBER next period, each row expandable to its 2–3 driving features. The scoring logic (the
  CPI-native + gap baselines plus the optional gradient-boosted challenger) lives in a plain Python
  module (`score.py`) that reads `9_HISTORICAL_DATA`, emits a ranked table, and **exposes precision@k
  and false-alerts-per-cycle** so the app and back-test read the same numbers; the app is a thin view
  over it.
- **Why this form:** the QS consumes this once per reporting cycle as a triage list, so a live,
  sortable screen beats a static report or a chat. Keeping the model in a separate module (not the UI)
  means the back-test notebook and the app share one scoring path, so there is no drift between what
  was validated and what is shown.
- **Not a Claude skill.** There is no natural-language step; it is a scheduled scoring + display job.
  An LLM adds latency and a hallucination surface for zero benefit here. (Asking questions *about* the
  watchlist is Idea 4's job, with this module wired in as a tool.)
- **Demo artifact:** the Streamlit watchlist + a one-page back-test notebook that uses **rolling-origin
  folds** (not a single split) and reports precision/recall, precision@5/@10, and false alerts per
  cycle for the GREEN→AMBER flip vs the CPI-native and gap baselines.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** Keep, but reframe hard. "Predict next period's Alert_Level" is the wrong target
because AMBER is sticky and Alert_Level is a deterministic threshold on the current period's own CPI
(verified: AMBER ≡ `CPI < 0.95`, 0 / 1,163 mismatch). A model that predicts "will be AMBER" mostly learns "is already AMBER" and demos
beautifully while doing nothing for the QS. The right problem is the **GREEN→AMBER transition** —
the moment a healthy centre tips. That is real QS pain in PROBLEM.md (the 50k pour that quietly
became 65k should have been flagged *as it started drifting*), and it is the only version of this
idea worth a demo.

**What already exists (don't rebuild blindly).** The panel hands you `Alert_Level`, `Risk_Flag`,
`CPI`, `Rolling_3M_CPI`, `EAC_vs_BAC_Ratio`, `Variance_Pct`, and both `Pct_Budget_Consumed` and
`Actual_Pct_Complete`. The label and nearly every feature are pre-computed. Textbook EVM already
gives a transparent leading signal: gap = budget-consumed minus percent-complete. Building an ML
classifier is justified **only if it beats a plain threshold on that gap** at forecasting the
transition. If it doesn't, the deliverable is the rule plus the watchlist UI, and that's still a win.

**Dream-state delta.** CURRENT: QS finds the overrun at month-end close, money already paid -->
THIS IDEA: a ranked watchlist names the GREEN centres about to tip, one period early, with the two
drivers --> 12-MONTH IDEAL: every reporting cycle opens with a triaged "act on these five" list the
QS trusts enough to chase before the invoices land.

**Approaches considered & pick.**
- A) *Transparent gap-threshold rule + watchlist* — effort S, low risk. Reuses gap, Rolling_3M_CPI,
  the transition labels. Fully explainable, no leakage surface, ships in the timebox.
- B) *Gradient-boosted transition classifier benchmarked against A* — effort M, medium risk (thin
  positives, leakage if split wrong). Reuses everything in A plus trend deltas and resource shares.
- **Chosen: A as the spine, B as the challenger.** Build the rule first so there is always a
  shippable, honest demo; add the tree and keep it only if it beats the rule on transition
  precision/recall by a margin worth the opacity. Reversible two-way door: yes (swap models freely).

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|-------------------------------|-------------|-----|
| D1 | Target = next-period AMBER, or GREEN→AMBER transition? | Reframe to transition | Sticky AMBER makes "next-period AMBER" a fake win; transitions are the QS's actual moment. |
| D2 | Is an ML model required at all? | Defer model, lead with rule | If gap-threshold matches the tree, ship the rule; model only earns its place by beating it. |
| D3 | Also predict `Risk_Flag = Medium`? | Cut for hackathon | Second label splits thin positives and muddies the demo; one crisp event beats two fuzzy ones. |
| D4 | Include NOT STARTED / zero-EV rows? | Cut before pairing | They dominate the majority class and inflate accuracy without informing transitions. |
| D5 | Row split for train/test? | Time-based by Period_ID | Row-shuffling leaks a centre's future into training and fakes the score. |

**Top failure modes.** 1) *Leakage via label rule* — feeding a t+1 signal that encodes Alert_Level;
the QS notices when the flag never fires early, only concurrently. 2) *Sticky-AMBER illusion* — high
accuracy that is really persistence; the QS notices the watchlist only ever lists centres already in
trouble. 3) *Cry-wolf precision* — too many false flags on a small positive set; the QS stops opening
the list after chasing two dead ends.

**Honest success metric.** Precision and recall of the **GREEN→AMBER flip under rolling-origin
validation** (across several target periods, not one held-out split), plus **precision@5/@10** and
**false alerts per reporting cycle**; lead time gained is descriptive, not the headline. It must beat
the **CPI-native baselines** (current CPI, distance to 0.95, one-period CPI change, rolling-CPI trend)
and the gap rule — not just the trivial "already AMBER" baseline — and a challenger only earns its keep
by beating held-out precision at equal recall by a predeclared margin. Report raw confusion counts
(positives are few). Leakage trap: split by period, never shuffle rows; never include a feature that
restates next period's label.

**Deferred to a real build (written down, not chosen).** Multi-class severity once RED data exists;
`Risk_Flag` co-prediction; survival-style "periods-until-tip" estimate; feeding the watchlist into
the Idea 4 copilot as a tool.

**Verdict.** BUILD-WITH-CHANGES — reframe the target to the GREEN→AMBER transition, lead with the
transparent gap rule, and make the model earn its keep against it.
