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

**Leakage warning built into the framing.** `Alert_Level` is almost certainly a deterministic
threshold on this period's own EVM columns (CPI / gap crossing a line). So do **not** feed the model
anything that encodes the label rule at time t+1, and expect that a good model is really *forecasting
whether next period's CPI/gap crosses the line*. Confirm the rule first: reverse-engineer what
`Alert_Level` is a function of on the current period (regress it on CPI, gap, Variance_Pct). If it's
a clean threshold, say so out loud in the demo, because then the honest task is "forecast the
crossing," and the naive baseline below is strong.

**Data used.** `9_HISTORICAL_DATA` only. Join the current row to its successor on
`BCC_ID` + consecutive `Period_ID` to create the (features_t → label_t+1) training pairs. Real
column names live on **row 5** of the sheet (rows 1–4 are banners — load with header=row 5).

**How you'd judge it's good.** Time-based split by `Period_ID`: train on the early periods, test on
the latest ones, never shuffle rows (shuffling leaks the future of the same centre into training).
There are **two baselines to beat, and the honest one is hard:**
1. *Trivial baseline* — "flag when already AMBER." Beating this is meaningless; it catches zero
   transitions by construction. Use it only to show why the transition framing is the real task.
2. *Real baseline* — **the transparent threshold rule** on `gap = Pct_Budget_Consumed −
   Actual_Pct_Complete` (e.g. flag GREEN centres where gap > X). This is the thing to beat. If the
   tree can't clear a plain gap threshold on the GREEN→AMBER event, ship the rule and say so.

Report, on GREEN→AMBER transitions only: **precision and recall of the newly-lit flag**, and
**lead time gained** (periods earlier the flag fires vs the trivial baseline). Precision matters
most; a QS ignores a watchlist that cries wolf. Judge on recall of the rare transition event, never
on overall accuracy (a "GREEN forever" predictor scores >90% and is useless).

**What the QS sees.** A ranked weekly/monthly **watchlist**: "These 6 BCCs are likely to go AMBER
next period," each with its top 2–3 driving features ("spending 18% ahead of progress; CPI trending
down 3 months"). One screen, sorted by risk.

**Build effort for a hackathon.** Low–medium. Labels and most features already exist in the sheet, so
it's mostly feature engineering + a standard classifier + a lead-time metric. A notebook plus a small
Streamlit table is a credible demo in the timebox.

**Risks / gotchas.** Only two live severity classes (GREEN, AMBER) and **no RED** — frame honestly as
GREEN→AMBER early detection, not multi-class severity. `Alert_Level` is likely a deterministic
threshold on current EVM, so the whole exercise is really *forecasting a crossing*; own that in the
demo rather than dressing it as pattern discovery. Thin positives: with ~174 centres × 12 periods and
only GREEN→AMBER flips counting, the event set is small, so a single lucky/unlucky test period can
swing the numbers. Report the confusion counts, not just rates. Class imbalance is severe (most rows
GREEN or NOT STARTED) — use class weights, judge on transition recall. Drop `NOT STARTED` and
zero-earned-value rows before building pairs so they don't dominate. Load `9_HISTORICAL_DATA` with
headers on **row 5** (rows 1–4 are banners: `header=4` / `skiprows=4`).

## Recommended deliverable

**Software dashboard** — a ranked watchlist screen, backed by a Python scoring module.
- **Form:** a Streamlit (or Plotly Dash) single screen: the sortable watchlist of GREEN centres likely
  to flip AMBER next period, each row expandable to its 2–3 driving features. The scoring logic (the
  gap rule plus the optional gradient-boosted challenger) lives in a plain Python module (`score.py`)
  that reads `9_HISTORICAL_DATA` and emits a ranked table; the app is a thin view over it.
- **Why this form:** the QS consumes this once per reporting cycle as a triage list, so a live,
  sortable screen beats a static report or a chat. Keeping the model in a separate module (not the UI)
  means the back-test notebook and the app share one scoring path, so there is no drift between what
  was validated and what is shown.
- **Not a Claude skill.** There is no natural-language step; it is a scheduled scoring + display job.
  An LLM adds latency and a hallucination surface for zero benefit here. (Asking questions *about* the
  watchlist is Idea 4's job, with this module wired in as a tool.)
- **Demo artifact:** the Streamlit watchlist + a one-page back-test notebook showing precision/recall
  of the GREEN→AMBER flip vs the plain gap baseline.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** Keep, but reframe hard. "Predict next period's Alert_Level" is the wrong target
because AMBER is sticky and Alert_Level is almost certainly a deterministic threshold on the current
period's own EVM. A model that predicts "will be AMBER" mostly learns "is already AMBER" and demos
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

**Honest success metric.** Precision and recall of the **GREEN→AMBER flip on a time-held-out test
period**, plus lead time gained, and it must **beat a plain gap threshold**, not just the trivial
"already AMBER" baseline. Report raw confusion counts (positives are few). Leakage trap: split by
period, never shuffle rows; never include a feature that restates next period's label.

**Deferred to a real build (written down, not chosen).** Multi-class severity once RED data exists;
`Risk_Flag` co-prediction; survival-style "periods-until-tip" estimate; feeding the watchlist into
the Idea 4 copilot as a tool.

**Verdict.** BUILD-WITH-CHANGES — reframe the target to the GREEN→AMBER transition, lead with the
transparent gap rule, and make the model earn its keep against it.
