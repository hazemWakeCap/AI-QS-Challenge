# 17 — Reading the sheet off the model: the IFC element → BOQ register

**Status:** built · **Date:** 2026-07-29
**Artifacts:** `data/ifc_boq_map.csv` · `tools/ifc_boq_map/generate_map.py` · `GET /api/v1/model/element-map`

---

## What this does

Click any element in the **IFC Take-off** tab and see what the bill of quantities says about it:
which BOQ items it consumes, their rates and quantities, the cost centres those items are, and
those centres' live earned value. From there, the existing cost-centre drawer — variance
attribution, forecast, correction actions — opens on a piece of geometry.

## Why it needed authoring at all

`school_str.ifc` is a genuine Autodesk Revit 2024 structural export. **Not one of its 1,526 elements
carries a cost code.** There is no `IfcElementQuantity`, no cost property set, no BOQ reference —
the information simply is not in the file, and no amount of parsing will find it. Equally, the
workbook's BOQ carries no `IfcGlobalId`. The two datasets share no key.

So the binding is declared, once, in a file a QS can read and argue with.

## Why one declared hop buys so much

The reason this is worth doing cheaply rather than elaborately:

```
IFC element ──(declared, this register)──▶ BOQ Item Ref
                                                │
                                                │  9_HISTORICAL_DATA.WBS_Code IS the BOQ Item Ref
                                                │  173 vs 173 · intersection 173 · zero orphans
                                                ▼
                                            BCC_ID ──▶ 12 periods of BAC / PV / EV / AC / CPI / alert
```

That second hop is **not authored** — it is an exact, bijective join already present in the source
data, verified in `IfcElementMapTests.The_boq_item_ref_and_the_wbs_code_are_the_same_key`. One
declared arrow buys the whole chain, and everything downstream of the BOQ item is real.

---

## Changes made to the source data

**None to the workbook. None to the model.**

| File | MD5 | Status |
|---|---|---|
| `data/Tower_X_Project_Data.xlsx` | `daabf42984ef59e909a5f46c5ee44cb4` | **unmodified** |
| `.../public/models/school_str.ifc` | `d7c500de611b85fea19ccfd92996cba3` | **unmodified** |
| `data/ifc_boq_map.csv` | `296cee259dcc95370d976d92ccae70f7` | new sidecar, 2,034 rows + header |

`CLAUDE.md` marks the workbook read-only source. Adding a sheet to it was considered and rejected:
it would fork the file we were handed from the one the importer's contract tests are pinned to
(`DataContractTests` asserts 2,076 rows / 173 cost centres). A CSV beside it stays diffable in git,
opens in the same spreadsheet, and can be hand-edited without touching source data.

---

## The mapping rules

One row per **(GlobalId, BOQ item)** — a physical element consumes more than one bill item.

| IFC class | Storey | BOQ item | Role | Elements | Confidence |
|---|---|---|---|---|---|
| IFCCOLUMN | any | 2.04 Columns concrete C40/50 | Concrete | 203 | 0.9 |
| IFCCOLUMN | any | 2.09 Column formwork | Formwork | 203 | 0.9 |
| IFCWALL | any | 2.05 Structural wall concrete | Concrete | 6 | 0.9 |
| IFCWALL | any | 2.10 Wall formwork | Formwork | 6 | 0.9 |
| IFCSLAB | any | 2.06 Suspended slab concrete | Concrete | 299 | 0.9 |
| IFCSLAB | any | 2.11 Slab soffit formwork | Formwork | 299 | 0.9 |
| IFCREINFORCINGBAR | `Sub Level` | 2.12 Rebar — raft foundations | Rebar | 560 | **0.6** |
| IFCREINFORCINGBAR | above ground | 2.14 Rebar — suspended slabs | Rebar | 59 | **0.6** |

**Coverage: 1,127 of 1,526 elements (74%)**, 1,635 mapped rows, reaching **8 cost centres**
(`BCC-STR-CON-204/205/206`, `BCC-STR-FWK-209/210/211`, `BCC-STR-RBR-212/214`).

---

## Assumptions — every one of them

### 1. The model is not the building the bill is for
`school_str.ifc` is a school; the bill is Tower X's. Binding one to the other is a **demo fixture**.
What is real is the mechanism and everything downstream of the BOQ item ref. The UI says so on the
page; this document says so here.

### 2. Rebar is placed by storey, because the file carries no host relationship
A bar reinforces a specific column, slab or wall, and the bill prices rebar separately for each
(2.12 raft / 2.13 columns / 2.14 slabs / 2.15 walls). **That relationship does not exist in this
file** — `IFCRELASSIGNSTOPRODUCT` and `IFCRELNESTS` both have zero occurrences. Storey is the only
signal left:

- 560 bars on `Sub Level` → **2.12 raft foundations**
- 59 bars above ground → **2.14 suspended slabs**

This is the weakest claim in the register and is carried at **confidence 0.6**, rendered at reduced
opacity in the 3D view, and stated in each row's `Basis`. Rebar to columns (2.13) and walls (2.15)
is never assigned, because nothing in the file could justify it.

### 3. Formwork rows are inferred, not measured
A concrete element implies its formwork item — a column implies column formwork. The register says
a column consumes 2.04 *and* 2.09; it does not measure the formwork area independently. Carried at
0.9 because the implication is a standard estimating relationship, not a guess about this model.

### 4. 399 elements are deliberately left unmapped
| Class | Count | Why |
|---|---|---|
| IFCBEAM | 375 | **The bill prices no beam concrete** — there is no beam item in any of the 18 sections |
| IFCMEMBER | 22 | No item in this bill covers the class |
| IFCPLATE | 2 | No item in this bill covers the class |

These are **reported as a scope gap, not as a mapping failure**: the model contains work the
estimate never priced. Pointing them at the slab or steel item so the picture fills in would attach
cost to scope the bill never carried, and inventing a number is the one thing this codebase
consistently refuses to do.

### 5. An element's confidence is its weakest binding
A slab bound at 0.9 for concrete and 0.9 for formwork reads 0.9. Had one been 0.6, the element
would read 0.6. An element is only as well-placed as its shakiest link.

### 6. An element's alert is its worst cost centre
A slab whose concrete is on budget and whose formwork is drifting is painted as drifting. Averaging
would hide it — the same aggregation trap the zone map's MIXED colour exists to avoid.

### 7. `9_HISTORICAL_DATA.Discipline` is truncated in the source data
Values are cut to 18 characters (`Architectural Fini`, `Communication Syst`). **Do not use it as a
join key or a display label.** Use `Package_Code` or the BOQ `Sec`. Not introduced here — it is a
property of the supplied workbook, recorded because it will bite the next person.

---

## Regenerating

```bash
python3 tools/ifc_boq_map/generate_map.py
```

Deterministic — run it twice and the diff is empty. It prints coverage, rows per BOQ item, and the
scope gap. No third-party dependency: the four fields needed (GlobalId, entity class, storey
containment, storey name) are read by regex from the STEP file, and a native IFC toolkit for four
fields would not pay for itself.

**Hand-editing is expected and supported.** A QS who disagrees that a column's formwork belongs to
2.09 can change that row. The join-integrity tests will fail the build if an edit points at a BOQ
item that does not exist or reaches no cost centre.

## Verification

`IfcElementMapTests` (11 facts) reads the committed CSV, not a fixture:

- every mapped item exists in `1_BOQ`
- every mapped item resolves to a cost centre through `WBS_Code`
- BOQ item refs and WBS codes are the same set, one centre per item
- coverage is 1,127 / 1,526 and mapped + unmapped accounts for every element
- beams are reported as scope the bill never priced
- rebar is the only thing carried below 0.9, and says why
- every rule ships a non-empty rationale
- commas inside a quoted rationale survive the CSV parser

## Known limits

- Coverage is capped at 74% by what Tower X's bill actually prices — not by the mechanism.
- The register binds one specific model to one specific project. Loading a different IFC through the
  file picker leaves it unresolved and the tab falls back to zone-level placement.
- `notInModel` counts register rows the loaded file does not contain; it is 0 for the bundled pair
  and is surfaced in the UI if the two ever drift apart.
