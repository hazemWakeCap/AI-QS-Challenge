# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

An **open-ended hackathon starter pack**, not an application. It ships a dataset and a
problem statement; there is no build system, no tests, and no application code yet. The
task (`PROBLEM.md`) is to **build something that helps a Quantity Surveyor (QS) see cost
trouble early** on a construction project — forecasting final cost, detecting budget drift,
or an angle nobody's thought of. No target metric, output format, or definition of "correct"
is prescribed; deciding *what* to solve and *how to judge it* is part of the work.

Any code, notebooks, or tooling you add is net-new. Choose the stack; there are no
conventions to match beyond what you establish.

## The data

Single workbook: `data/Tower_X_Project_Data.xlsx`. All amounts in **AED**; a "shift" = **10 working hours**.
`DATA_DICTIONARY.md` is the authoritative column-by-column reference — read it before touching the data.

| Sheet | Rows | Grain |
|-------|------|-------|
| `1_BOQ` | ~232 | One priced work item (bill of quantities) |
| `2_ESTIMATE_NORMS` | ~211 | One estimating "recipe" (output + resource consumption per unit of work) |
| `3_BOQ_MAPPING` | ~194 | BOQ line → norm → estimate package |
| `4_ESTIMATE_DATASHEET` | ~794 | BOQ item exploded into resource lines; **unit rates live here** |
| `9_HISTORICAL_DATA` | ~2,097 | One cost centre per reporting period (~2,100 month-by-month records) |

Load with `openpyxl` (already installed) or `pandas.read_excel(..., sheet_name=...)`.

### How the sheets connect
- **`Norm Code`** joins `2_ESTIMATE_NORMS` ↔ `3_BOQ_MAPPING` ↔ `4_ESTIMATE_DATASHEET`; **`Norm Ref`** on `1_BOQ` points at the same norms.
- **`BOQ Sec` + `Item`** identify a BOQ line across sheets 1, 3, and 4.
- The cost model **reconciles end to end**: resource costs in `4_ESTIMATE_DATASHEET` roll up (via `Total Contract Amt`) to the `TOTAL Amount` on `1_BOQ`. Direct cost flows estimate → BOQ, which then adds margin + contingency to reach the contract total. If a derived figure doesn't tie out, suspect your join/aggregation before the data.

### Two data gotchas (from `data/README.md`)
- **Estimate quantities** in `4_ESTIMATE_DATASHEET`: manpower/equipment qty = `BOQ qty × (gang or equipment count ÷ Output Norm)`, **not** `BOQ qty × gang size`. Missing the Output Norm divisor overstates labour/equipment cost. Materials and subcontract scale with quantity (not shifts).
- The project's **computed budget, earned-value, KPI, and progress sheets are intentionally not included** — only sheets 1–4 and 9. Any EVM output for Tower X must be derived, not looked up. `9_HISTORICAL_DATA` is the only sheet that already carries computed EVM (BAC/PV/EV/AC, CPI/SPI, EAC/VAC, alert/risk flags) — it's the training/reference bank for patterns over time.

## Domain vocabulary (EVM)

The problem is Earned Value Management. Core identities:
`CV = EV − AC` · `CPI = EV ÷ AC` · `SPI = EV ÷ PV` · `EAC` = forecast final cost · `VAC = BAC − EAC`.
`DATA_DICTIONARY.md` has the full glossary. "Seeing trouble early" ≈ catching CPI/SPI drift or a rising `EAC_vs_BAC_Ratio` before month-end close.

## Conventions

- Treat `data/Tower_X_Project_Data.xlsx`, `DATA_DICTIONARY.md`, and `PROBLEM.md` as **read-only source**. Don't mutate the workbook in place; write derived outputs to new files.
- `.codeboarding/` is tooling metadata — ignore it for the task.
