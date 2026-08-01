# Feature 14 — IFC Take-off

## What it is

Point the system at an IFC model and it measures what it can, prices it with the project's own
rate library, compares the model's quantities against the bill's, and reports — in full — what
it could **not** price and why. The unpriced residual is the interesting output: it is scope the
estimate never carried.

## Who it's for

The QS doing a pre-award sanity check against a designer's model, or anyone asking "does the
model agree with the bill?"

## How it works

Four steps, each of which reports its own failures rather than absorbing them.

**1 — Measure** (`model/ifcMeasure.ts`). Volume and area are read from IFC property sets. The
bundled sample carries **no `IfcElementQuantity` at all** and uses Spanish parameter group names,
so the reader ships a multi-locale synonym table and reports whether standard base quantities
were available or whether it fell back.

**2 — Price** (`Core/Model/{TakeoffRateMap,RateBook,TakeoffPricer}.cs`). Four **declared** rules
map an IFC class and a measure to a BOQ item and its rate:

| IFC class | Measure | BOQ item |
|---|---|---|
| IFCCOLUMN | volume | 2.04 columns concrete C40/50 |
| IFCWALL | volume | 2.05 structural wall concrete |
| IFCSLAB | volume | 2.06 suspended slab concrete |
| IFCSLAB | area | 2.11 slab soffit formwork |

A unit-agreement guard normalises m³/m² so a volume rate can never be applied to an area
quantity. Deliberate gaps — IFCBEAM, IFCREINFORCINGBAR — are left unpriced and land in a visible
residual. Rates used are **direct + indirect only**; margin and contingency are excluded, because
this is a cost take-off, not a price.

**3 — Reconcile.** The tie-out is `priced + unpriced + unmeasured == totalElements`, with the
element count derived **independently of the pricing pass** so a pricing bug cannot hide behind a
matching total.

**4 — Compare to the bill.** Model quantities are grouped by BOQ item and compared with the
bill's: `variance = modelQty − boqQty`, `costImpact = variance × unitRate`. Where the bill
carries no quantity for an item, it is reported as **uncomparable** — never as a 100% overrun.

**Locatability** (`model/ifcCostLink.ts`) is graded in two tiers: **Direct 0.9** where an element
property literally names a zone code, **Grouped 0.4** where only a class-and-storey rule places
it. `model/ifcZoneMap.ts` declares 14 such rules and reports its **match rate** rather than
claiming a placement.

## API

| Endpoint | Purpose |
|----------|---------|
| `POST /api/v1/model/price-takeoff` | Prices measured take-off lines with the project rate book (max 500 lines). |

## UI

The **IFC Take-off** tab: the loaded model, a rules table ("why this pairing"), *Priced at
Tower X's rates*, *What could be priced*, *What could not — and why*, *Does the model agree with
the bill?*, *Can this model be measured?*, *Could this model be located in the cost plan?*, a
zone table, and *Measured by class*. Elements can be selected to read the bill off them
(see [17 — IFC → BOQ element register](17-ifc-boq-element-map.md)).

## Guarantees & limits

- **The tie-out is asserted by tests** (`TakeoffPricingTests`, 15 facts).
- **The bundled model is a school, not Tower X**, and the tab says so on the page. The two
  buildings are unrelated. What is demonstrated is that a rate library travels to an arbitrary
  model — the AED total is a mechanism demonstration, not a valuation of anything.
- **The unpriced residual is a feature, not a gap to close.** Pointing unpriced classes at the
  nearest item would attach cost to scope the estimate never carried.
- Measurement depends on what the exporter wrote. A model with no quantities and no recognisable
  parameter names will report itself unmeasurable rather than guess.

## On Tower X

`school_str.ifc` is a genuine Autodesk Revit 2024 structural export of **1,526 elements**. Priced
with Tower X's rate library it comes to **4,638,842 AED** of priceable scope from **883
measurable elements (58%)**:

| Class → item | Quantity | Rate | Amount |
|---|---|---|---|
| IFCSLAB → 2.06 | 2,735.5 m³ | 1,122.59 | 3,070,815 AED |
| IFCSLAB → 2.11 | 6,761.8 m² | 181.80 | 1,229,315 AED |
| IFCWALL → 2.05 | 127 m³ | 1,459.66 | 185,443 AED |
| IFCCOLUMN → 2.04 | 112.3 m³ | 1,364.28 | 153,268 AED |

And the reconciliation, printed on the page: **508 priced + 375 unpriced + 643 unmeasured =
1,526 elements**. The 375 unpriced are beams — Tower X's bill prices no beam concrete in any of
its 18 sections.
