# Idea 2 — Probabilistic Cost-Trajectory Forecaster

**One-line pitch.** A calibrated cost forecast that beats the textbook `EAC = BAC ÷ CPI` where the
formula is weakest (early, noisy periods), shown as a shaded cone (P10 / P50 / P90) that tightens as
a cost centre progresses.

**Reality check that shapes everything below.** This project's panel (`9_HISTORICAL_DATA`, 173
centres × 12 periods) is a mid-flight snapshot, not a bank of finished jobs. Median centre progress at
the *last* period is **13%**; only **4 of 173** centres ever reach ~100%; last-period AC/BAC median is
**0.134**. So "known final cost" barely exists in this data. A final-cost (EAC) back-test has almost no
ground truth to score against. The honest, validatable target here is **short-horizon** cumulative
cost (1-3 periods ahead), where every one of the 2,088 centre-period rows gives real ground truth.
Final-cost is still drawn as a cone, but labelled a directional extrapolation, not a validated number.

**The QS pain it kills.** `EAC = BAC ÷ CPI` assumes today's cost performance holds to the end. It
whipsaws early (when CPI is noisy, here up to **102** from tiny early AC) and reports a single number
with no uncertainty. A QS reporting cost to the client needs a defensible trajectory *and* a
confidence band, and needs to know *when* the number is trustworthy versus noise.

**Approach.** Model cumulative cost in **cost space**, never by dividing by CPI (that is what
explodes). Two nested claims:
- **Validated core — short-horizon forecast.** For each centre at period *k*, predict
  `AC_AED_Cumulative` at *k+1 … k+3* from features observed up to *k*. Back-test across all
  centre-periods (~2,088 rows), score against realized cumulative AC. This has honest ground truth.
- **Directional overlay — final-cost cone.** Extrapolate the fitted trajectory to 100% progress for
  the on-screen cone. Label it explicitly "not yet validatable on this project (too early)" because
  169/173 centres never finish in-window. Do **not** claim it beats the formula on final cost.
- **Band method — split-conformal residuals**, not a learned quantile ensemble. Take held-out
  short-horizon residuals, bin by progress, and read P10/P90 from their empirical quantiles. This is
  calibrated *by construction* and honest on the sample size we actually have.

**Data used.** `9_HISTORICAL_DATA`, keyed by `BCC_ID` + `Period_ID` (12 periods). Features at period
*k*: `Actual_Pct_Complete`, `Rolling_3M_CPI` (prefer over raw `CPI`), `CV_AED`, `AC_AED_Cumulative`,
`EV_AED`, `BAC_AED`, resource-split AC columns. Label = realized `AC_AED_Cumulative` at *k+h*. Header
on **row 5** (`header=4`). **Leakage guard:** never feed `EAC_AED`, `VAC_AED`, or `EAC_vs_BAC_Ratio` —
`EAC_AED` is *exactly* `BAC/CPI` in this data (verified: 100% of rows within 1%), so it leaks the very
formula we benchmark against.

**How you'd judge it's good.** Rolling-origin back-test on the short-horizon target. Metrics: **MAPE
of the P50** at *h*=1,2,3; **band coverage** (fraction of held-out truths inside P10-P90, target ~80%,
*reported not asserted*, with a reliability plot). Baselines to beat, at the same origin: (1) formula
carry-forward `AC_{k+h} = planned_EV_{k+h} / CPI_k`; (2) naive random-walk `AC_{k+h} = AC_k`; and
critically (3) **`AC_{k+h} = AC_k + planned spend`**. The win is "lower MAPE than all three in the
early-progress band, with honest coverage." Split centres train/test so calibration is out-of-sample.

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
  realized `AC_AED_Cumulative`. Never use it as a feature.
- **CPI outliers to 102** from tiny early AC. Forecast in cost space, use `Rolling_3M_CPI`, clip/flag
  progress < ~10%.
- **Biased completer set:** the 4 finishers are likely the small/fast centres; don't let a lifecycle
  curve fit on them speak for big long-running packages.

## Recommended deliverable

**Software dashboard, with the back-test notebook as the credibility artifact.**
- **Form:** a Streamlit app showing the per-package **cost cone** (P50 line + shaded P10–P90 band over
  BAC/PV) with the "forecast validatable / too early" trust badge, plus a project roll-up (Monte-Carlo
  over centres). The forecasting + conformal-band logic sits in a Python module (`forecast.py`); the
  rolling-origin back-test lives in a notebook that produces the MAPE-vs-baselines table and the
  coverage reliability plot.
- **Why this form:** a forecast a QS shows a client has to *look* trustworthy and be *provably*
  trustworthy. The dashboard is the look (a tightening cone reads instantly); the notebook is the
  proof (measured coverage, beats the three baselines). Ship both — the notebook is what answers "nice
  chart, but is it right?"
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
- A) Minimal viable — short-horizon (h=1..3) P50 forecast in cost space + split-conformal band, rolling
  back-test vs 3 baselines. Effort S-M, low risk, reuses `Rolling_3M_CPI`/`AC_Cumulative`/`BAC`.
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
| D3 | Probabilistic band vs point forecast? | Add (short-horizon only) | 2,088 rows can calibrate a 1-3 period band; a defensible range beats a lone number for the client. |
| D4 | Project roll-up by summing quantiles? | Cut → Monte Carlo | Summed P90s assume perfect correlation; MC across centres is barely more code. |
| D5 | Use `EAC_AED` / `EAC_vs_BAC_Ratio` as features? | Cut | `EAC_AED == BAC/CPI` exactly; leaks the baseline and the label. |

**Top failure modes.** 1) *Final-cost claim with no truth* — model looks confident on a project that's
13% done; QS notices when a flagship long-running package's cone is wildly off and nothing ever
validated it. Fix: badge it directional. 2) *Circular scoring against `EAC_AED`* — you "beat the
formula" by scoring against the formula; QS never notices (silent). Fix: score only vs realized
`AC_AED_Cumulative`. 3) *CPI-102 blow-up* — dividing by a near-zero early CPI throws EAC to 10x BAC;
QS notices an absurd early number and stops trusting the tool. Fix: cost-space, `Rolling_3M_CPI`, clip
progress < 10%. 4) *Oversold band* — claiming 80% coverage when the reliability plot shows 55%; QS
loses trust the first time truth lands outside P10-P90. Fix: report measured coverage, don't assert it.

**Honest success metric.** Short-horizon **P50 MAPE at h=1,2,3**, beating all three baselines (formula
carry-forward, random-walk, planned-spend) in the sub-40%-progress band, *plus* measured band coverage
near 80% on out-of-sample centres. Leakage traps to avoid: features strictly from periods ≤ k; label
from k+h realized AC only; exclude `EAC_AED`/`VAC`/`EAC_vs_BAC_Ratio`; train/test split by centre so
the conformal band is not in-sample; keep the planned-spend baseline in so a near-budget project can't
make a lazy model look smart.

**Deferred to a real build (written down, not chosen).** Learned quantile/GBM final-cost model;
correlation-modelled Monte Carlo roll-up; cross-project generalization (only one project here);
schedule coupling via SPI; revisiting final-cost validation once the project matures past ~50%.

**Verdict.** BUILD-WITH-CHANGES — reframe from "validated final-cost band" to "calibrated
short-horizon cost forecast with a directional final cone"; that is the honest, demoable win on this
data.
