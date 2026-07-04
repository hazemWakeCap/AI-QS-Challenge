# AI Quantity Surveyor — 5 Prototype Ideas

Five genuinely different directions for the challenge in `PROBLEM.md` ("help a QS see cost trouble
early"). They span the solution space on purpose — detection, forecasting, pre-execution validation,
interface, and diagnosis — so this is a choice of *direction*, not five flavours of one thing.

| # | Idea | Angle | Leans on | Impact | Build effort | Wow |
|---|------|-------|----------|--------|--------------|-----|
| [1](idea-1-early-warning-classifier.md) | Early-warning drift classifier | Detection (predict next-period AMBER) | `9_HISTORICAL_DATA` | High | Low–Med | Med |
| [2](idea-2-eac-forecaster.md) | Probabilistic final-cost forecaster | Forecasting (P10/P50/P90 EAC) | `9_HISTORICAL_DATA` | High | Med | Med |
| [3](idea-3-should-cost-auditor.md) | Bottom-up should-cost auditor | Pre-execution estimate validation | Sheets 1–4 (+9 cross-check) | Med–High | Med | Med |
| [4](idea-4-qs-copilot.md) | QS copilot (agent over the data) | Interface / Claude agent | All sheets via tools | High | Med–High | **High** |
| [5](idea-5-variance-root-cause.md) | Variance root-cause decomposer | Explainability / diagnosis | `9` + `4_ESTIMATE_DATASHEET` | Med–High | Med | Med |

## One-liners
1. **Early-warning classifier** — flags a cost centre a month before it turns AMBER, from this period's signals.
2. **Final-cost forecaster** — a "cost cone" (P10/P50/P90) that beats textbook `EAC = BAC/CPI` and tightens over time.
3. **Should-cost auditor** — rebuilds each package from norms + rates to catch estimates that were optimistic on day zero.
4. **QS copilot** — a Claude agent you ask in plain English; it does the EVM math in tools and cites its sources.
5. **Root-cause decomposer** — turns "over budget" into "over because manpower is 1.8× norm," via variance attribution.

## Recommendation

- **Top pick: Idea 1 (early-warning classifier)** — best signal-per-effort for a hackathon. The
  labels (`Alert_Level`, `Risk_Flag`) and most features (`Rolling_3M_CPI`, `Variance_Pct`,
  `EAC_vs_BAC_Ratio`) already exist in the panel, the "lead time gained vs the naive rule" metric is
  clean and honest, and the output — a ranked watchlist — is instantly legible to a QS.
- **Highest-wow demo: Idea 4 (copilot)** — the most impressive thing to show live, and it can wrap
  Ideas 1/2/5 as tools, so it doubles as a front-end to the whole suite.
- **Most differentiated: Idea 3 (should-cost auditor)** — the only idea that mines the estimate sheets
  and attacks the problem *before* execution; strongest "we saw an angle others missed" story, at the
  cost of the most data-plumbing.

**Suggested combo if time allows:** build Idea 1 first (fast, measurable), then expose it — plus a
simple forecast (Idea 2) and a drill-down (Idea 5) — behind the Idea 4 copilot for the demo.

## Notes for whoever builds
- `9_HISTORICAL_DATA` has 4 banner rows; real headers are on **row 5** (`header=4` / `skiprows=4`).
- Panel shape: **174 cost centres × 12 periods** (Oct-2025→Sep-2026); last two periods anchored to
  Tower X's actual progress. Severity labels are GREEN/AMBER only (no RED); risk is Low/Medium only.
- Apply the **Output-Norm quantity correction** from `data/README.md` in any bottom-up cost math
  (Ideas 3 and 5): manpower/equipment qty = `BOQ qty × count ÷ Output Norm`.
- Join keys: `Norm Code` (norms↔mapping↔datasheet), `BOQ Sec`+`Item` (to BOQ), `BCC_ID`+`Period_ID`
  (history panel), `Package_Code`/`Estimate Package` (history↔estimate).
