# `9_HISTORICAL_DATA` — Column-by-Column Reference

The sheet's grain is **one row per BCC per month** (Tower X has 173 BCCs × 12 months ≈ 2,100 rows). The 38 columns fall into 7 groups. Formulas below were verified against the data (BCC-ARC-MAS-301, BAC 3,444,671, Budget_Qty 5,400).

---

## A. Identifiers & Classification — *the link keys*
This is where the sheet connects to the other four tables.

| Column | Meaning | Links to |
|---|---|---|
| `Row_ID` | Surrogate row number | internal only |
| `Period_ID` | Month index 1–12 | — |
| `Month_Year` | Calendar label (Oct-2025 …) | — |
| `BCC_ID` | The cost centre account (`BCC-ARC-MAS-301`) | **its own key**; code mirrors Discipline+Package |
| `WBS_Code` | = the BOQ item ref (`3.01`) | `1_BOQ` Item Ref · `3_BOQ_MAPPING` Item · `4_ESTIMATE_DATASHEET` Item |
| `Zone_Area` | Physical location (`FLOORS-B2-RF`) | *historical-only* — no counterpart in estimate sheets |
| `Discipline` | Discipline name | `2_ESTIMATE_NORMS` Discipline · BOQ groupings |
| `Package_Code` | = Estimate Package (`EP-ARC-MAS`) | `3_BOQ_MAPPING` Estimate Package · `4_ESTIMATE_DATASHEET` Package |

**`WBS_Code` and `Package_Code` are the two joins back to the estimate.**

---

## B. Budget Parameters — *seeded from the estimate*
Copied in from `1_BOQ` when the cost centre is created; unchanged month to month.

| Column | Meaning | Links to |
|---|---|---|
| `BAC_AED` | Budget At Completion — the cost budget | = `1_BOQ` **Direct+Indirect Amount** (margin/contingency stripped — you track against *cost*, not sell price) |
| `Budget_Qty` | Total units to build | = `1_BOQ` Quantity (5400) |
| `Unit` | Unit of work | = BOQ / Norm Unit (m²) |
| `Direct_Unit_Cost_AED` | Cost per unit | = `1_BOQ` Direct+Indirect Unit (637.9) |
| `Margin_Pct` | Mark-up % | = `1_BOQ` Margin % (20) |

---

## C. Plan (Scheduled) — *the baseline curve*
What *should* be done by this month. All cumulative.

| Column | Meaning | Formula (verified) |
|---|---|---|
| `Plan_Pct_Complete` | Cumulative planned % complete | baseline schedule input |
| `Planned_Qty_Period` | Planned quantity to date | = Plan_Pct% × `Budget_Qty` |
| `PV_AED` | **Planned Value** | = Plan_Pct% × `BAC` |

---

## D. Actuals (Measured) — *what really happened*
The only truly "actual" data in the whole workbook.

| Column | Meaning | Formula (verified) |
|---|---|---|
| `Actual_Pct_Complete` | Cumulative actual % complete | measured on site |
| `Earned_Qty_Period` | Earned quantity to date | = Actual_Pct% × `Budget_Qty` |
| `Earned_Qty_Cumul` | Cumulative earned quantity | same as above |
| `EV_AED` | **Earned Value** = budget × %done | = Actual_Pct% × `BAC` |
| `AC_AED_Period` | Actual cost booked | measured |
| `AC_AED_Cumulative` | Cumulative actual cost | running sum |

---

## E. EVM Metrics & Variance — *computed from C + D*

| Column | Meaning | Formula (verified) |
|---|---|---|
| `CV_AED` | Cost Variance (− = over budget) | = EV − AC |
| `SV_AED` | Schedule Variance (− = behind) | = EV − PV |
| `CPI` | Cost Performance Index | = EV ÷ AC |
| `SPI` | Schedule Performance Index | = EV ÷ PV |
| `EAC_AED` | Estimate At Completion (forecast final cost) | ≈ BAC ÷ CPI |
| `VAC_AED` | Variance At Completion | = BAC − EAC |
| `Pct_Budget_Consumed` | % of budget spent | = AC_Cumulative ÷ BAC |

Example (Nov-2025, BCC-ARC-MAS-301): EV 16,879 − AC 17,688 = **CV −809**; EV ÷ AC = **CPI 0.954**; EV ÷ PV = **SPI 0.620** → slightly over cost and behind schedule.

---

## F. Resource Cost Split — *AC broken down by type*
Splits the actual cost into the four resource types.

| Column | Links to |
|---|---|
| `AC_Material_AED` | `4_ESTIMATE_DATASHEET` Resource Type = **MATERIAL** |
| `AC_Manpower_AED` | … = **MANPOWER** |
| `AC_Equipment_AED` | … = **EQUIPMENT** |
| `AC_Subcontract_AED` | … = **SUBCONTRACT** |

The four sum to `AC_AED_Period`. This is the column group that lets you compare **actual vs estimated cost *per resource type*** — the datasheet holds the estimated split, this holds the actual split. That's exactly the join you'd use for variance root-cause ("did the overrun come from labour or material?").

---

## G. Alerts & AI/ML Signals — *derived flags, sheet-internal*
Computed from the metrics above; no link to other sheets.

| Column | Meaning |
|---|---|
| `Alert_Level` | Status label (`NOT STARTED`, on-track, warning, critical…) |
| `Variance_Pct` | Variance as a % of budget |
| `Risk_Flag` | Boolean/flag for at-risk cost centres |
| `Rolling_3M_CPI` | 3-month moving-average CPI (trend, not snapshot) |
| `EAC_vs_BAC_Ratio` | EAC ÷ BAC (>1 = forecast overrun) |

---

## How Sheet 9 relates to the rest — one picture

```
 1_BOQ ─────────────┐  (Direct+Indirect Amount → BAC, Qty → Budget_Qty, Unit, Unit Cost, Margin)
   │ Item Ref 3.01  │
   ▼                ▼
 3_BOQ_MAPPING   9_HISTORICAL_DATA
   │ Est.Package     │  WBS_Code ◄── BOQ Item Ref
   ▼                 │  Package_Code ◄── Estimate Package
 4_ESTIMATE_DATASHEET│  Discipline ◄── Discipline
   │ Resource Type ──┘  AC_Material/Manpower/Equipment/Subcontract ◄── Resource Type
   ▼
 2_ESTIMATE_NORMS (recipe behind the datasheet rates)
```

- **Down the left** = the estimate (plan/price).
- **Sheet 9** = the same activities tracked over time — it **pulls its budget from `1_BOQ`**, **joins on `WBS_Code` / `Package_Code` / `Discipline`**, and **mirrors the datasheet's resource split** with actual costs.

## Two caveats from the actual data
1. **The `*_Period` columns store cumulative values here.** `Planned_Qty_Period`, `Earned_Qty_Period`, and `AC_AED_Period` are *equal to* their cumulative counterparts in every row — despite the "Period" name, they are not per-month increments in this dataset. If you need the true monthly delta, compute `row − previous row` yourself.
2. **Percentages are stored as percent, not fraction** — `0.79` means 0.79%, so PV = `Plan_Pct/100 × BAC`.
