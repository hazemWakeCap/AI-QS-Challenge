# Dataset — Tower X

The inputs for one building project plus a bank of historical records. Full column definitions are in [`../DATA_DICTIONARY.md`](../DATA_DICTIONARY.md). Amounts in **AED**; a "shift" = **10 working hours**.

Sheets included:
- `1_BOQ` — bill of quantities
- `2_ESTIMATE_NORMS` — productivity/resource norms
- `3_BOQ_MAPPING` — BOQ line → norm mapping
- `4_ESTIMATE_DATASHEET` — resource-level cost build-up (with rates)
- `9_HISTORICAL_DATA` — ~2,100 month-by-month records from past work

The project's computed budget, earned-value, KPI and progress sheets are intentionally **not** included.

## Data note — correction applied (28 Jun 2026)
An earlier version had a costing error: in `4_ESTIMATE_DATASHEET`, manpower and equipment quantities were computed as `BOQ quantity × gang size` **without dividing by the operation's Output Norm** (units per gang-shift), which overstated labour/equipment — and the derived costs — on most line items.

This has been **corrected**: manpower/equipment resource quantities are now `BOQ quantity × (gang or equipment count ÷ Output Norm)`. Materials and subcontract (which scale with quantity, not shifts) were already correct and are unchanged. After the fix the model reconciles end to end — BOQ direct cost = estimate build-up, budgets recompute from the corrected direct cost, and all earned-value performance ratios (CPI/SPI) are preserved. Unit rates now sit in a realistic range.

> This correction was made by the organising team and is pending final sign-off from the cost-control lead. As with any real project data, validate figures you depend on.
