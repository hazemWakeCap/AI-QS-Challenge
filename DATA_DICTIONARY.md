# Data Dictionary

The dataset describes one building project ("Tower X"), plus a large bank of historical records from past work. Amounts are in **AED**. This dictionary explains what each sheet and column means — it does **not** tell you what to build with them.

## A few core terms
- **BOQ (Bill of Quantities):** the priced list of work items on the project — how much of each thing has to be built.
- **Norm:** a "recipe" for an operation — how much output one gang produces per shift, and the labour, materials, and equipment it consumes per unit of work.
- **BCC (Budget Cost Centre):** a bucket the budget and spending are tracked against.
- **Unit of Work (UoW):** the measure for an item — m², m³, linear m, No., etc.
- **EVM terms:** **BAC** = budget at completion (target cost) · **PV** = planned value · **EV** = earned value (budget × % work done) · **AC** = actual cost · **CV** = cost variance (EV − AC) · **SV** = schedule variance · **CPI** = EV ÷ AC · **SPI** = EV ÷ PV · **EAC** = estimate at completion (forecast final cost) · **VAC** = variance at completion (BAC − EAC).

---

## Sheet 1 — `1_BOQ` (Bill of Quantities)
The priced contract bill. One row per work item.

| Column | Meaning |
|--------|---------|
| Sec | Section number (discipline grouping) |
| Item Ref | Item number (e.g. 1.01) |
| Description | What the item is |
| Unit | Unit of work (m², m³, etc.) |
| Quantity | Total units of work required |
| Direct+Indirect Unit | Cost per unit before margin/contingency |
| Direct+Indirect Amount | Quantity × the above |
| Margin % / Margin Amount | Mark-up applied |
| Cont % / Contingency Amount | Contingency allowance |
| TOTAL Unit Price / TOTAL Amount | All-in price per unit and total |
| Norm Ref | Link to the estimating norm used (Sheet 2) |

## Sheet 2 — `2_ESTIMATE_NORMS` (Norms library)
The "recipes." One row per operation. Figures are **per one unit of work** and are meant to be multiplied by BOQ quantities.

| Column | Meaning |
|--------|---------|
| Disc Code / Discipline Name | Discipline (CIV, STR, …) |
| Sub-Trade Code / Sub-Trade Name | Trade within the discipline |
| Norm Code | Unique norm ID (links to BOQ Norm Ref & mapping) |
| Operation / Activity | What's being done |
| Unit | Unit of work |
| Output Norm | Units produced per gang-shift (a shift = 10 hours) |
| Procurement Route | Self-perform vs subcontract |
| Manpower — Gang Composition | The crew and its makeup |
| Gang Size | Number of people in the gang |
| Material 1/2 — Description, Qty/UoW, Unit | Material consumed per unit of work |
| Equipment 1 | Plant/equipment used |
| SC Trade / Notes | Subcontract scope + conditions/adjustments (e.g. "−30% for confined spaces") |

## Sheet 3 — `3_BOQ_MAPPING` (BOQ → norm)
How each BOQ line connects to a norm and an estimate package. One row per BOQ item.

| Column | Meaning |
|--------|---------|
| BOQ Sec / Item / Description / Unit | The BOQ line being mapped |
| Norm Code | Which norm applies |
| Estimate Package | Package the work rolls up into (e.g. EP-CIV-DEMO) |
| Op Code | Operation code |
| Primary Resource Types | Manpower / Material / Equipment / Subcontract mix |
| Procurement | Self-perform or subcontract |
| Notes | Context |

## Sheet 4 — `4_ESTIMATE_DATASHEET` (resource-level cost build-up)
The detailed estimate — each BOQ item exploded into its resource lines, with rates. Multiple rows per BOQ item (one per resource type).

| Column | Meaning |
|--------|---------|
| BOQ Sec / Item / Description / Unit | The BOQ line |
| BOQ Qty | Quantity from the BOQ |
| Norm Code / Package / Op Code | Links to norm and package |
| Resource Type | MANPOWER / MATERIAL / EQUIPMENT / SUBCONTRACT |
| Resource Description | The specific resource |
| Qty / Unit Work | Resource needed per unit of work |
| Consumption Unit | Unit for that consumption |
| Total Resource Qty | Resource quantity across the whole item |
| **Unit Rate (AED)** | Cost per resource unit (the rates live here) |
| Resource Cost (AED) | Total Resource Qty × Unit Rate |
| Indirect Cost (AED) | Allocated indirect cost |
| Total Contract Amt (AED) | The item's contract value — rolls up to the BOQ TOTAL Amount (Sheet 1, col M) |
| Contract Unit Price (AED) | Total Contract Amt ÷ BOQ Qty — the all-in contract price per unit |
| Gang Output / Gang Size | From the norm |
| Notes | Conditions |

## Sheet 9 — `9_HISTORICAL_DATA` (past project records)
~2,100 month-by-month records from past work — the bank of history. One row per cost centre per reporting period. Useful if you want patterns over time. Column groups:

- **Identifiers:** Row_ID, Period_ID, Month_Year, BCC_ID, WBS_Code, Zone_Area, Discipline, Package_Code
- **Budget:** BAC_AED, Budget_Qty, Unit, Direct_Unit_Cost_AED, Margin_Pct
- **Plan:** Plan_Pct_Complete, Planned_Qty_Period, PV_AED
- **Actuals:** Actual_Pct_Complete, Earned_Qty_Period, Earned_Qty_Cumul, EV_AED, AC_AED_Period, AC_AED_Cumulative
- **EVM metrics:** CV_AED, SV_AED, CPI, SPI, EAC_AED, VAC_AED, Pct_Budget_Consumed
- **Resource split:** AC_Material_AED, AC_Manpower_AED, AC_Equipment_AED, AC_Subcontract_AED
- **Signals:** Alert_Level, Variance_Pct, Risk_Flag, Rolling_3M_CPI, EAC_vs_BAC_Ratio

---

*All figures are illustrative project data. A "shift" throughout = 10 working hours.*
