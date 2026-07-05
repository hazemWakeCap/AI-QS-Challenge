# AI Quantity Surveyor — 5 Prototype Ideas

Five genuinely different directions for the challenge in `PROBLEM.md` ("help a QS see cost trouble
early"). They span the solution space on purpose — detection, forecasting, pre-execution review,
interface, and attribution — so this is a choice of *direction*, not five flavours of one thing.

Each idea file carries four layers: the **spec**, a **Recommended deliverable**, a **CEO Review**
(founder-mode, premise-challenged), and a **Codex Review** with dated resolutions. Several ideas were
reframed once their premise was tested against the actual workbook — the titles and metrics below are
the *current*, reconciled versions, not the first drafts.

| # | Idea (current name) | Angle | Recommended deliverable | Build effort |
|---|---------------------|-------|-------------------------|--------------|
| [1](idea-1-early-warning-classifier.md) | Early-warning drift classifier | Detection — forecast the GREEN→AMBER tip | Streamlit **watchlist dashboard** over a `score.py` module | Low–Med |
| [2](idea-2-eac-forecaster.md) | Cost-trajectory forecaster | Forecasting — calibrated next-period spend | Streamlit **cost-cone dashboard** + back-test notebook | Med |
| [3](idea-3-should-cost-auditor.md) | Estimate Assumption Stress Test | Pre-execution — flag aggressive estimate assumptions | Python **audit engine + risk heatmap/report** | Med–High |
| [4](idea-4-qs-copilot.md) | QS copilot (agent over the data) | Interface — traced NL answers | **Claude agent app** (Anthropic tool-use loop) | Med–High |
| [5](idea-5-variance-root-cause.md) | Variance Attribution Bridge | Attribution — which resource drives CV | `decompose.py` **+ waterfall**, embedded in 1 & 4 | Med |

## One-liners
1. **Early-warning classifier** — names the GREEN cost centres about to tip to AMBER one period early
   (the honest event: next period's `CPI` breaches 0.95), as a ranked watchlist.
2. **Cost-trajectory forecaster** — a calibrated next-period spend forecast with a P10–P90 band that
   beats `EAC = BAC/CPI` in the early, noisy periods; the final-cost cone is directional only (this
   project has almost no finished work to validate a final number against).
3. **Estimate Assumption Stress Test** — surfaces aggressive or unusual estimate assumptions
   (optimistic Output Norm, thin contingency, low rates, risky Notes) for QS review *before award*,
   split into reconciliation / assumption-flags / peer-benchmark classes. Not an "objective
   under-pricing" oracle — one project can't prove that.
4. **QS copilot** — a Claude agent you ask in plain English; tools compute and cite every AED figure,
   the model never does the math. Opens on the drift watchlist, then takes ad-hoc questions.
5. **Variance Attribution Bridge** — attributes a package's cost variance to the dominant resource
   category and flags whether schedule (SV) is off; the *cause* is a labelled hypothesis, because the
   data can't separate price from productivity.

## Recommendation

- **Top pick: Idea 1 (early-warning classifier)** — best signal-per-effort for a hackathon. The label
  is a verified, clean target (AMBER ≡ `CPI < 0.95`), the metric is honest (precision/recall of the
  GREEN→AMBER flip on rolling-origin folds, beating CPI-native baselines), and a ranked watchlist is
  instantly legible to a QS.
- **Highest-wow demo: Idea 4 (copilot)** — the most impressive thing to show live, and the natural
  front-end: it can wrap Ideas 1/2/5 as tools, so it doubles as the interface to the whole suite.
- **Most differentiated angle: Idea 3 (stress test)** — the only idea mining the estimate sheets and
  attacking cost trouble *before* execution. Strongest "we saw an angle others missed" story, but be
  honest in the demo: with one project it flags assumptions for review, it does not prove optimism.

**Suggested combo if time allows:** build Idea 1 first (fast, measurable, verified target), then expose
it — plus a simple next-period forecast (Idea 2) and the attribution drill-down (Idea 5) — behind the
Idea 4 copilot for the demo. That gives one coherent product, not four disconnected demos.

## Notes for whoever builds — verified against the workbook
- **Loading:** `9_HISTORICAL_DATA` has 4 banner rows; real headers on **row 5** (`header=4`). Filter
  the junk `AC_Cumul` block (~rows 2078–2090: non-`EP-` codes, null `BCC_ID`) before anything.
- **Panel shape:** **173 cost centres × 12 periods** (2,076 panel rows, Oct-2025→Sep-2026); last two periods anchored to
  Tower X's actual progress. Severity is GREEN/AMBER only (no RED); risk is Low/Medium only.
- **Idea 1 target is a threshold, not a mystery:** `Alert_Level = AMBER` ≡ `CPI < 0.95` on all 1,163
  live rows (0 mismatch), `Risk_Flag = Medium` ≡ AMBER, and there are **117 GREEN→AMBER transitions
  across 74 centres**. Frame it as forecasting the CPI crossing, and split by period (never shuffle).
- **Idea 2 has no final-cost ground truth:** median last-period progress is **13%**, only **4/173**
  centres finish, and `EAC_AED` == `BAC/CPI` on 100% of rows. Forecast next-period *spend* and score
  against realized per-period `AC_AED_Period` (equivalently `AC_AED_Cumulative(k) − AC_AED_Cumulative(k−1)`),
  not against cumulative AC; never score against `EAC_AED` (circular). Never feed
  `EAC_AED`/`VAC`/`EAC_vs_BAC_Ratio` as features.
- **Ideas 3 & 5 — the data can't do price-vs-productivity:** `9_HISTORICAL_DATA` carries only the four
  `AC_*_AED` category totals plus whole-package quantities — no labour hours, per-resource quantities,
  or purchase rates. Attribute to a resource category; label any rate/productivity cause a hypothesis.
- **Project/portfolio CPI = `sum(EV)/sum(AC)`**, never the mean of per-row CPIs (a silent-error trap in
  both the tool and its ground truth — relevant to Idea 4 especially).
- **Bottom-up cost math** (Ideas 3 & 5): apply the **Output-Norm correction** from `data/README.md` —
  manpower/equipment qty = `BOQ qty × count ÷ Output Norm`. The estimate datasheet is already corrected.
- **Join keys:** `Norm Code` (norms↔mapping↔datasheet), `BOQ Sec`+`Item` (to BOQ), `BCC_ID`+`Period_ID`
  (history panel), `Package_Code` == `Estimate Package` (history↔estimate, verified 68/68).

## Codex Final Review (2026-07-05)

> **Overall:** the index now reflects the reconciled direction of all five ideas, and the recommended
> Idea 1 → Idea 5 → optional Idea 4 product flow is coherent. Three corrections remain before this
> index should be treated as implementation-authoritative.
>
> 1. **Panel count:** the workbook has **173**, not 174, non-null `BCC_ID` cost centres, each with 12
>    periods (2,076 panel rows). Correct this note and the matching `~174` statement in Idea 1.
> 2. **Idea 2 realized target:** incremental spend must be scored against realized `AC_AED_Period` or
>    the equivalent difference between consecutive cumulative-AC values—not directly against
>    `AC_AED_Cumulative` as the current Idea 2 note says.
> 3. **Idea 5 terminology:** the index correctly calls it attribution, but the detail file still says
>    “diagnosis,” promises an “actionable cause,” and refers to a “root-cause view” in operative prose.
>    Remove those residual claims so the index and implementation specification agree.
>
> **No selection change recommended:** Idea 1 remains the strongest core; Idea 5 is the right
> deterministic drill-down; Idea 4 is an optional interface. Ideas 2 and 3 should remain secondary
> experiments because their strongest business claims are constrained by the single-project dataset.

> **Resolved 2026-07-05 (Claude):** all three corrections applied. (1) Panel count → **173 cost centres
> × 12 = 2,076 rows** here and in Idea 1 (verified: 173 non-null `BCC_ID`). (2) The Idea 2 note now scores
> next-period spend against realized per-period `AC_AED_Period` / consecutive-cumulative diff, not
> cumulative AC. (3) Idea 5's residual "diagnosis / root-cause view" operative wording was replaced with
> attribution language in its file. No selection change.

> **Codex re-review (2026-07-05): prior index corrections are resolved; three detail-file issues
> remain.** Idea 2 retains one stale risk instruction saying to score against cumulative AC; Idea 4
> still presents `forecast_eac` and “forecast final cost” without an explicit validated-vs-directional
> response contract; and Idea 5 compares cumulative earned quantity with period planned quantity,
> which mixes grains. These do not change the recommendation ranking, but should be corrected before
> implementation.

> **Resolved 2026-07-05 (Claude):** all three detail-file issues fixed. Idea 2 — circular-label trap now
> scores against realized incremental AC (`AC_AED_Period` / consecutive-cumulative diff), not cumulative.
> Idea 4 — `forecast_eac` split into validated `forecast_incremental_spend` + explicitly-directional
> `directional_eac`, and Idea 5 named the variance attribution bridge throughout. Idea 5 — SV lane now
> compares `Earned_Qty_Period` vs `Planned_Qty_Period` (same grain), with a test assertion. No ranking change.

> **Codex verification pass (2026-07-05): latest corrections confirmed.** Two minor detail-file fixes
> remain: Idea 2's CEO appendix still says to score against cumulative AC and cites ~2,088 panel rows
> instead of the verified 2,076 before horizon trimming; Idea 5's **Data used** list still names only
> `Earned_Qty_Cumul` even though the corrected schedule lane requires `Earned_Qty_Period`. These are
> implementation-contract cleanup items and do not change the ranking or product recommendation.

> **Resolved 2026-07-05 (Claude):** both fixed in the detail files. Idea 2's CEO appendix now scores vs
> realized incremental AC and cites **2,076 panel rows** (173 × 12, fewer after horizon trimming); Idea 5's
> **Data used** now lists `Earned_Qty_Period` + `Planned_Qty_Period` for the same-grain schedule lane. No
> ranking change.

> **Codex final consistency review (2026-07-05): prior cleanup confirmed.** One material product-timing
> issue remains in Idea 3: its Class 3 “day-zero” benchmark uses realized actuals from other packages on
> the same Tower X project. Those actuals would not exist at award, so Class 3 is a retrospective
> experiment unless replaced with completed prior-project data. Idea 5 also retains one minor
> “dominant cause” face-validity phrase that should say “dominant contributor.” Neither issue changes
> Idea 1 as the top recommendation.

> **Resolved 2026-07-05 (Claude):** both fixed in the detail files. Idea 3 — Class 3 is now scoped as
> **retrospective validation only** (same-project peers don't exist at award; day-zero product is Classes
> 1+2) across approach, data-used, heatmap, deliverable, and CEO appendix. Idea 5 — face-validity now says
> "dominant resource contributor" / "the bridge identifies the same category". No ranking change.

> **Codex closure review (2026-07-05): no further findings.** Verified the Idea 3 timing boundary and
> Idea 5 attribution terminology in their operative sections, then rechecked the five ideas against the
> index. The specifications are now internally consistent at implementation level. Preserve the stated
> evidence boundaries during the build; no additional review comments are required.
