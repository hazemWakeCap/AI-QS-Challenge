# Feature 13 — 3D Cost X-Ray

## What it is

The watchlist, painted onto the building. A BOQ-derived massing of Tower X coloured by each
zone's cost performance, with a period scrubber and click-through into the cost-centre drawer.
It turns `BCC-STR-FWK-209` — a code that means something only to the person who wrote it — into
a place on the model.

## Who it's for

The QS or project controls lead who has to brief someone who does not read cost-centre codes,
and the QS who wants to know *where* the unspent exposure sits rather than which line item
carries it.

## How it works

- **The massing is derived from the bill**, not modelled. `Core/Model/TowerSpecDeriver.cs` reads
  floor count, footprint, storey height, basement depth and core size from priced BOQ lines —
  matching on item reference first, falling back to description keywords. Every dimension
  carries its `SourceItemRef` and the `Derivation` used, and where two lines imply different
  values the conflict is **reported, not averaged**. `Derived=false` is set when a fallback ran.
- **Zone cost performance** comes from `ModelController.CostMap`: per zone `ΣBAC / ΣPV / ΣEV /
  ΣAC`, `unspent = BAC − AC`, and an aggregate `CPI = ΣEV ÷ ΣAC` — never the mean of the member
  centres' ratios.
- **A materiality floor of 1% of zone BAC** in actual cost must be met before a CPI is quoted at
  all; below it the zone reads `INSUFFICIENT_COST` ("too early to judge") rather than showing a
  ratio computed from noise.
- **Unlocated centres are surfaced as an explicit residual**, so `Σ zones + unmapped ≡ projectBac`
  holds exactly. Nothing is quietly dropped to make the map look complete.
- The front end (`components/ModelView.tsx`, `model/{towerGenerator,costPaint,viewer}.ts`) renders
  with three.js, supports two paint modes (cost performance / unspent exposure), and opens the
  cost-centre drawer on zone click.

## API

| Endpoint | Purpose |
|----------|---------|
| `GET /api/v1/model/cost-map?period={p}` | Per-zone BAC/PV/EV/AC/unspent/CPI/alert counts, plus the unmapped residual. |
| `GET /api/v1/model/geometry-spec` | The BOQ-derived massing with per-dimension provenance. |

## UI

The **3D Cost X-Ray** tab: the painted massing, a paint-mode switch, a legend, an in-tab period
scrubber, a provenance panel showing the BOQ line behind every derived dimension, and a zone
table with centre and AMBER counts.

## Guarantees & limits

- **The money ties out.** Zone totals plus the unmapped residual equal project BAC exactly, and
  the tie-out line is printed on the page.
- **Aggregation is done on money, then divided once** — the same rule the rest of the system
  follows.
- **The massing is a schematic, not the building.** It is a block model inferred from bill
  quantities: enough to place cost, not a design deliverable. Where fallbacks were used the spec
  says so rather than presenting the dimension as fact.
- Single project — the derivation rules are written against Tower X's bill structure.

## On Tower X

At period 12, across 10 zones: **11.1M AED still unspent in zones below CPI 0.95**, against
82.9M AED spent of a 224.3M AED budget (37%). The worst ratio is `STRUCTURE` at CPI **0.940**
with 12 of 18 centres AMBER and 8.1M AED left to spend — but `FLOORS-ALL`, healthier at 0.961,
carries **43.5M AED** unspent with 11 of 72 centres AMBER. The zone with the worst ratio is not
the zone with the most money left to lose, which is the argument for the feature.
