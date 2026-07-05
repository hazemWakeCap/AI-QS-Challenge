# Idea 2 — Probabilistic Incremental-Spend Forecaster (short-horizon cost cone)

**One-line pitch.** A calibrated forecast of **next-period incremental spend** (h=1,2,3) that beats
the textbook `EAC = BAC ÷ CPI` where the formula is weakest (early, noisy periods), shown as a shaded
band (P10 / P50 / P90) that tightens as a cost centre progresses. Cumulative cost is a derived display
only.

**Reality check that shapes everything below.** This project's panel (`9_HISTORICAL_DATA`, 173
centres × 12 periods) is a mid-flight snapshot, not a bank of finished jobs. Median centre progress at
the *last* period is **13%**; only **4 of 173** centres ever reach ~100%; last-period AC/BAC median is
**0.134**. So "known final cost" barely exists in this data. A final-cost (EAC) back-test has almost no
ground truth to score against. The honest, validatable target here is **short-horizon incremental
spend** (the AC added over 1-3 periods ahead), where the 2,076 panel rows (173 × 12) give real ground
truth — though the usable labelled examples are fewer and horizon-dependent, since the last `h` periods
of each centre have no `k+h` label; cumulative cost is just the running sum for display. Final-cost is still drawable as
a cone, but subordinated and labelled a directional extrapolation, not a validated number.

**The QS pain it kills.** `EAC = BAC ÷ CPI` assumes today's cost performance holds to the end. It
whipsaws early (when CPI is noisy, here up to **102** from tiny early AC) and reports a single number
with no uncertainty. A QS reporting cost to the client needs a defensible trajectory *and* a
confidence band, and needs to know *when* the number is trustworthy versus noise.

**Approach.** Predict in **cost space**, never by dividing by CPI (that is what explodes). Two nested
claims:
- **Validated core — incremental-spend forecast.** For each centre at period *k*, predict the
  **incremental AC** added in each of the next periods, evaluated at horizons *h*=1, 2, 3 **separately**.
  Back-test across the panel (2,076 centre-period rows, minus the final `h` periods per centre that have
  no `k+h` label), score against realized incremental spend.
  Cumulative AC is reconstructed as `AC_k` + summed increments purely for the display cone. This has
  honest ground truth and avoids the persistence flattery of scoring cumulative cost directly.
- **Directional overlay — final-cost cone (subordinated).** Extrapolate the fitted trajectory to 100%
  progress for an optional on-screen cone, hidden or visually de-emphasised by default. Label it
  explicitly "not yet validatable on this project (too early)" because 169/173 centres never finish
  in-window. Do **not** claim it beats the formula on final cost.
- **Band method — split-conformal residuals**, not a learned quantile ensemble. Take held-out
  incremental-spend residuals, bin by progress, and read P10/P90 from their empirical quantiles. This
  is calibrated *by construction* and honest on the sample size we actually have.

**Data used.** `9_HISTORICAL_DATA`, keyed by `BCC_ID` + `Period_ID` (12 periods). Features at period
*k*: `Actual_Pct_Complete`, `Rolling_3M_CPI` (prefer over raw `CPI`), `CV_AED`, `AC_AED_Cumulative`,
`EV_AED`, `BAC_AED`, recent-period spend, resource-split AC columns. Label = realized **incremental
spend** at *k+h* (`AC_AED_Cumulative` at *k+h* minus at *k+h−1*); cumulative AC is derived for display,
not the scored target. Header on **row 5** (`header=4`). **Leakage guard:** never feed `EAC_AED`,
`VAC_AED`, or `EAC_vs_BAC_Ratio` —
`EAC_AED` is *exactly* `BAC/CPI` in this data (verified: 100% of rows within 1%), so it leaks the very
formula we benchmark against.

**How you'd judge it's good.** **Grouped rolling-origin** back-test on the incremental-spend target:
train and calibrate only on strictly earlier periods, evaluate later ones, and keep those same future
residuals out of the conformal calibration set — a centre-only split is not enough to defeat time
dependence. Primary metrics: **MAE as a % of BAC** and/or **WAPE** of the P50 at *h*=1,2,3; report
**MAPE only after a defensible minimum-cost/progress gate** (it is unstable exactly in the low-cost
early band this idea targets). Plus **band coverage** (fraction of held-out truths inside P10-P90,
target ~80%, *reported not asserted*, with a reliability plot). Baselines to beat, at the same origin:
(1) **zero-increment / random-walk** (`increment=0`, i.e. `AC_{k+h}=AC_k`); (2) **planned-spend**
(`increment = planned spend`); (3) **recent-spend run-rate** (last observed period's spend carried
forward); (4) **CPI-based** (`planned_EV_{k+h} / CPI_k`). The win is "lower MAE-%BAC/WAPE than all four
in the early-progress band, with honest coverage."

**What the QS sees.** A per-package **cost cone**: P50 line with a shaded P10-P90 band, overlaid on
BAC and PV, that narrows as progress grows. A trust badge per centre ("forecast validatable" vs "too
early / no analog"). Project roll-up via **Monte Carlo across centres** (not summed quantiles), with an
over/under-budget probability and a caveat that correlation is assumed independent.

**Build effort for a hackathon.** Medium. The short-horizon back-test + conformal band is the
demoable, honest centrepiece and is straightforward. The final-cost cone is a cheap visual overlay.
The learned quantile ensemble is cut (can't be calibrated on 4 completers).

**Risks / gotchas.**
- **No final-cost ground truth** (median 13% complete, 4 completers). The final EAC claim is
  unfalsifiable here; keep it directional, headline the short-horizon number.
- **Circular label trap:** `EAC_AED == BAC/CPI` exactly. Never score against `EAC_AED`; only against
  realized **incremental** AC (`AC_AED_Period`, or the consecutive-cumulative difference) — not the
  persistent cumulative series. Never use `EAC_AED` as a feature.
- **CPI outliers to 102** from tiny early AC. Forecast in cost space, use `Rolling_3M_CPI`, clip/flag
  progress < ~10%.
- **Biased completer set:** the 4 finishers are likely the small/fast centres; don't let a lifecycle
  curve fit on them speak for big long-running packages.

## Codex Review — Findings and Recommendations (2026-07-05)

> **Checked 2026-07-05 (Claude): sound, no correction needed.** The "four completers" point matches the
> earlier data check (4 / 173 centres reach ~100%); the MAPE-instability-at-low-cost, cumulative-cost-
> persistence, and strictly-earlier-calibration-residuals points are valid refinements. Adopt the WAPE /
> MAE-as-%-of-BAC metric and the next-period-incremental-spend target as implementation guidance on top
> of the CEO-review reframe.

> **Codex follow-up (2026-07-05) — acknowledged but not folded into the operative spec.** The main
> **Approach**, **Data used**, and **How you'd judge it's good** sections still make cumulative AC the
> target, MAPE the primary metric, and a centre split the validation design. The deliverable still
> headlines a final-cost cone and promises an MAPE table. Replace those instructions with incremental
> spend, MAE-as-%-BAC/WAPE, grouped rolling-origin calibration, and the expanded baselines before
> handing this file to an implementation agent.

> **Resolved 2026-07-05 (Claude):** propagated through the operative spec — title, pitch, reality-check,
> Approach, Data used, How-you'd-judge-it, What-the-QS-sees, Recommended deliverable, and the CEO-review
> appendix now make next-period incremental spend the primary target (h=1,2,3 separately, cumulative AC
> a derived display), replace primary MAPE with MAE-%BAC/WAPE (MAPE only past a minimum-cost/progress
> gate), require grouped rolling-origin validation with strictly-earlier conformal residuals, expand to
> the four baselines (zero-increment/random-walk, planned-spend, recent-spend run-rate, CPI-based), and
> headline the validated incremental-spend band with the final-cost cone hidden/subordinated.

> **Codex final check (2026-07-05): ready, with one cross-file correction.** This file correctly defines
> the target as `AC_AED_Cumulative(k+h) − AC_AED_Cumulative(k+h−1)`. Update `INDEX.md`, which currently
> says to score next-period spend directly against realized cumulative AC. No additional change is
> needed to this idea's operative specification.

> **Resolved 2026-07-05 (Claude):** confirmed this file is already correct; fixed the cross-file issue in
> `INDEX.md`, whose Idea 2 note now scores next-period spend against realized per-period `AC_AED_Period`
> (equivalently the consecutive-cumulative diff), not directly against cumulative AC.

> **Codex re-review (2026-07-05): one stale operative sentence remains.** Under **Risks / gotchas**, the
> circular-label warning still says “only against realized `AC_AED_Cumulative`.” Change it to realized
> incremental AC (`AC_AED_Period` or the consecutive-cumulative difference). As written, it contradicts
> the corrected target and could cause the implementation to score the persistent cumulative series.

> **Resolved 2026-07-05 (Claude):** fixed. The Risks/gotchas circular-label trap now says score only
> against realized **incremental** AC (`AC_AED_Period` / consecutive-cumulative diff), not the cumulative
> series.

> **Codex verification pass (2026-07-05): operative core resolved; appendix cleanup remains.** The CEO
> Review's **Top failure modes** still says “score only vs realized `AC_AED_Cumulative`”; change it to
> incremental AC. Also replace the repeated `~2,088` row claim with **2,076 panel rows before creating
> horizon labels**; usable examples will be fewer and horizon-dependent after dropping the final
> `h` periods for each centre.

> **Resolved 2026-07-05 (Claude):** CEO-appendix failure mode #2 now says score vs realized incremental
> AC (`AC_AED_Period` / diff), not cumulative; all `~2,088` row claims corrected to **2,076 panel rows
> (173 × 12)** with the note that labelled examples are fewer after dropping each centre's final `h` periods.

### Findings

- The short-horizon reframe is correct: only four cost centres reach at least 99% completion, so a
  final-cost model cannot be credibly validated here.
- Scoring cumulative AC can flatter a model because cumulative cost is persistent and changes slowly.
  The random-walk baseline may appear strong without producing a useful spend forecast.
- MAPE is unstable for early, low-cost observations—the exact region this idea intends to improve.
- A centre-only train/test split does not fully protect against time dependence. Calibration residuals
  must also be strictly earlier than the periods being evaluated.
- A visually prominent final-cost cone may be remembered as the product claim even when labeled
  "directional," creating a mismatch between the validated result and the demo.

### Recommendations for the implementation agent

1. Make **next-period incremental spend** the primary target and retain cumulative AC as a derived
   display. Evaluate horizons 1–3 separately.
2. Replace primary MAPE with MAE as a percentage of BAC and/or WAPE; report MAPE only after imposing a
   defensible minimum-cost/progress gate.
3. Use grouped rolling-origin validation: train and calibrate on earlier periods, evaluate later
   periods, and prevent the same future residuals from entering conformal calibration.
4. Compare against zero-increment/random-walk, planned-spend, recent-spend run rate, and CPI-based
   baselines.
5. Make the validated 1–3-period band the headline. Hide or visually subordinate the final-cost
   extrapolation unless the demo explicitly explains that it has no adequate final-cost ground truth.

## Recommended deliverable

**Software dashboard, with the incremental-spend back-test notebook as the credibility artifact.**
- **Form:** a Streamlit app headlining the validated **1-3-period incremental-spend band** (P50 line +
  shaded P10–P90, with cumulative AC reconstructed for a display cone over BAC/PV) and the "forecast
  validatable / too early" trust badge, plus a project roll-up (Monte-Carlo over centres). The
  final-cost extrapolation is hidden or visually subordinated unless the demo explicitly states it has
  no adequate final-cost ground truth. The forecasting + conformal-band logic sits in a Python module
  (`forecast.py`); the grouped rolling-origin back-test lives in a notebook that produces the
  **MAE-%BAC / WAPE-vs-baselines table** (four baselines) and the coverage reliability plot.
- **Why this form:** a forecast a QS shows a client has to *look* trustworthy and be *provably*
  trustworthy. The dashboard is the look (a tightening band reads instantly); the notebook is the
  proof (measured coverage, beats all four baselines on MAE-%BAC/WAPE). Ship both — the notebook is
  what answers "nice chart, but is it right?"
- **Not a Claude skill.** This is numerical modelling and charting; an LLM has no role in producing or
  displaying the forecast. (Surfacing "what's my forecast for BCC-X?" in words is Idea 4, wrapping
  `forecast.py` as a tool.)
- **Demo artifact:** the cone dashboard for two contrasting packages (one early/noisy, one further
  along) + the back-test notebook.

---

## CEO Review (founder-mode, auto-answered 2026-07-04)

**Premise verdict.** REFRAME (build with changes). The instinct is right, `EAC = BAC/CPI` is a bad
early forecast, but the spec as written is built on a false assumption: that this dataset holds
completed centres to score a *final-cost* forecast against. It does not. Verified: median last-period
progress is 13%, only 4 of 173 centres finish, `EAC_AED` is literally `BAC/CPI` (100% of rows). So the
truncation-to-final back-test has almost no ground truth, and "beats the formula on final cost" is
unfalsifiable here. Reframe the product to the claim the data *can* support: a calibrated
**short-horizon** cost forecast (1-3 periods) that beats the formula in the early, noisy band, with the
final-cost cone kept as an explicitly-directional overlay. A QS gets more from a trustworthy near-term
number with an honest band than from a confident final number no one can check.

**What already exists (don't rebuild blindly).** `CPI`, `Rolling_3M_CPI`, `CV_AED`, `EAC_AED`,
`EAC_vs_BAC_Ratio`, `Alert_Level` are pre-computed. `EAC_AED` is the textbook formula verbatim, so it
is the *baseline*, not a feature and not a label. Building on top is justified: the raw columns give
you a point EAC with no uncertainty and no notion of *when* it is trustworthy. The value added is
calibration (conformal band) and a validated horizon, which the panel does not hand you.

**Dream-state delta.** CURRENT: QS quotes a single BAC/CPI EAC that whipsaws month to month. THIS
IDEA: a calibrated near-term cost cone with a "trustworthy yet?" badge, plus a directional final cone.
12-MONTH IDEAL: every cost report to the client carries a defensible range and a stated confidence,
and the QS knows which packages' forecasts to trust.

**Approaches considered & pick.**
- A) Minimal viable — short-horizon (h=1..3, evaluated separately) **incremental-spend** P50 forecast
  in cost space + split-conformal band, grouped rolling-origin back-test vs 4 baselines. Effort S-M,
  low risk, reuses `Rolling_3M_CPI`/`AC_Cumulative`/`BAC`.
- B) Ideal — learned quantile GBM / conformalized ensemble predicting final AC with correlation-aware
  Monte Carlo roll-up. Effort L, high risk: 4 completers cannot calibrate a final-cost interval.
- **Chosen: A**, plus the final-cost cone as a labelled directional overlay, because it is the only
  claim this data can honestly validate and it still demos as a tightening cost cone. Reversible
  two-way door: yes (swap in a learned model later without changing the interface).

**Scope decisions (auto-answered).**
| # | Question the CEO review posed | Auto-answer | Why |
|---|---|---|---|
| D1 | Final-cost EAC as the headline, validated claim? | Cut (keep as directional overlay) | No ground truth: 4/173 completers, median 13% done. Unfalsifiable here. |
| D2 | Learned quantile ensemble for P10/P90? | Defer | Can't calibrate on 4 completers; conformal residuals are honest now. |
| D3 | Probabilistic band vs point forecast? | Add (short-horizon only) | 2,076 panel rows can calibrate a 1-3 period band; a defensible range beats a lone number for the client. |
| D4 | Project roll-up by summing quantiles? | Cut → Monte Carlo | Summed P90s assume perfect correlation; MC across centres is barely more code. |
| D5 | Use `EAC_AED` / `EAC_vs_BAC_Ratio` as features? | Cut | `EAC_AED == BAC/CPI` exactly; leaks the baseline and the label. |

**Top failure modes.** 1) *Final-cost claim with no truth* — model looks confident on a project that's
13% done; QS notices when a flagship long-running package's cone is wildly off and nothing ever
validated it. Fix: badge it directional. 2) *Circular scoring against `EAC_AED`* — you "beat the
formula" by scoring against the formula; QS never notices (silent). Fix: score only vs realized
**incremental** AC (`AC_AED_Period` / consecutive-cumulative diff), never `EAC_AED`. 3) *CPI-102 blow-up* — dividing by a near-zero early CPI throws EAC to 10x BAC;
QS notices an absurd early number and stops trusting the tool. Fix: cost-space, `Rolling_3M_CPI`, clip
progress < 10%. 4) *Oversold band* — claiming 80% coverage when the reliability plot shows 55%; QS
loses trust the first time truth lands outside P10-P90. Fix: report measured coverage, don't assert it.

**Honest success metric.** Short-horizon **incremental-spend P50 MAE-as-%-of-BAC / WAPE at h=1,2,3**
(MAPE only past a minimum-cost/progress gate), beating all four baselines (zero-increment/random-walk,
planned-spend, recent-spend run-rate, CPI-based) in the sub-40%-progress band, *plus* measured band
coverage near 80% on out-of-sample centres. Leakage traps to avoid: features strictly from periods ≤ k;
label from k+h realized incremental AC only; exclude `EAC_AED`/`VAC`/`EAC_vs_BAC_Ratio`; use grouped
rolling-origin so conformal residuals are strictly earlier than the evaluated periods (a centre split
alone is not enough); keep the planned-spend baseline in so a near-budget project can't make a lazy
model look smart.

**Deferred to a real build (written down, not chosen).** Learned quantile/GBM final-cost model;
correlation-modelled Monte Carlo roll-up; cross-project generalization (only one project here);
schedule coupling via SPI; revisiting final-cost validation once the project matures past ~50%.

**Verdict.** BUILD-WITH-CHANGES — reframe from "validated final-cost band" to "calibrated
short-horizon cost forecast with a directional final cone"; that is the honest, demoable win on this
data.
