# Dataset — Tower X

The inputs for one building project plus a bank of historical records. Full column definitions are in [`../DATA_DICTIONARY.md`](../DATA_DICTIONARY.md). Amounts in **AED**; a "shift" = **10 working hours**.

Sheets included:
- `1_BOQ` — bill of quantities
- `2_ESTIMATE_NORMS` — productivity/resource norms
- `3_BOQ_MAPPING` — BOQ line → norm mapping
- `4_ESTIMATE_DATASHEET` — resource-level cost build-up (with rates)
- `9_HISTORICAL_DATA` — ~2,100 month-by-month records from past work

The project's computed budget, earned-value, KPI and progress sheets are intentionally **not** included.

## Data note — current cost model (29 Jun 2026)
This dataset is generated from the project's latest reconciled cost model. Two things worth knowing:

- **Estimate quantities.** In `4_ESTIMATE_DATASHEET`, manpower and equipment resource quantities are computed as `BOQ quantity × (gang or equipment count ÷ Output Norm)` (units produced per gang-shift) — **not** `BOQ quantity × gang size`. An earlier draft omitted the Output Norm divisor, which overstated labour/equipment and the costs derived from them; that has been corrected. Materials and subcontract scale with quantity (not shifts) and are unchanged.
- **Pricing.** Direct cost flows from the estimate build-up into the BOQ, which adds margin and contingency to reach the contract total. The model reconciles end to end: BOQ direct cost = estimate build-up, and the contract amounts in `4_ESTIMATE_DATASHEET` roll up to the BOQ totals. Unit rates sit in a realistic range.

> This data was prepared and reconciled by the organising team. As with any real project data, validate figures you depend on.
