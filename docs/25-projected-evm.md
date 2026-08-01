# Feature 17 — Projected EVM

## What it is

The cost-centre EVM row, continued past the last reported period. The take-off tab's slider already
ran to period 21 — [feature 16](24-forecast-build-sequence.md) put it there — but every cost figure
beside it was pinned at period 12, and **Open BCC-…** opened the drawer at period 12 no matter where
you had scrubbed. Scrub to 14 and the building rose while CPI, EV, AC and EAC sat still.

This makes those figures move. At any period the slider reaches, the side panel and the drawer show
a projected EVM position for the centre, with its band and what stands behind it.

It answers the question the frozen panel refused: *if the work keeps going at this pace and the money
keeps going at that pace, what does this centre look like in three months?*

## Who it's for

The QS who wants the number before month-end close, not after it. A CPI that slides from 0.95 to
0.90 across periods 13–15 is a conversation with a subcontractor in period 13; the same figure read
in period 15 is a variation claim.

## Why this was allowed, when the previous rule said no

The take-off tab carried an explicit refusal:

> The workbook has no rows beyond the origin, so CPI, EV, AC and the drawer all resolve to the last
> measured period… this feature forecasts progress, and a projected percentage is not grounds for
> inventing a cost.

That rule was too broad, and the schema is what shows it. From `db/migrations/0002_schema.sql`:

```sql
pv_amount GENERATED ALWAYS AS (round(planned_pct          / 100.0 * bac_amount, 2)) STORED,
ev_amount GENERATED ALWAYS AS (round(actual_pct_complete  / 100.0 * bac_amount, 2)) STORED,
```

**Earned value is not an independent quantity.** It is percent complete times budget — a definition,
evaluated by the database. Applying that same identity to a *projected* percentage restates a
projection we already publish; it does not introduce a second model.

What the rule correctly forbids is deriving **spend** from progress. So that line is kept exactly:

| Figure | Where it comes from | If the source is silent |
|---|---|---|
| % complete | `ProgressForecaster` (back-tested, 1.85 / 3.22 / 3.90 pp MAE) | no row at all |
| EV | `BAC × pct/100` — the schema's identity | — |
| AC | `IncrementalSpendForecaster`'s cumulative cone | `acAvailable: false`, AC null |
| CPI, EAC, VAC, CV | EV and AC together | null, with AC |
| PV, SPI | **nowhere** | always null past the origin |

CPI is the ratio of two *independent* projections, so it moves. That movement is the whole feature:
one model says how fast the work is going, another says how fast the money is going, and the gap
between them is the early warning.

## The three bases

| Basis | Periods (Tower X) | What stands behind it |
|---|---|---|
| `Measured` | ≤ 12 | Reported — a passthrough of `/api/v1/cost-centres`, pinned field-by-field by test. (Over the wire this endpoint rounds ratios to 4dp, as its sibling forecast routes do; the amounts are unchanged.) |
| `Forecast` | 13 – 15 | Both engines inside their back-tested horizons. Published error bars on each. |
| `Extrapolated` | 16 – 21 | Past the spend model's reach. Same arithmetic, no measured accuracy. |

A row takes the **weaker** of its two legs: a back-tested progress point married to an extrapolated
spend figure is an extrapolated row and says so.

## Two assumptions, both stated on every response

**1. Past period 15, cost performance is carried.** `ForecastConfig.Horizons` is frozen at {1, 2, 3},
so the spend cone stops at period 15. Beyond it the remaining work is priced at the CPI the cone ends
on — the classic directional cost-to-complete, already used in this namespace for
`DirectionalFinalCost` and `QsAnalyticsTools.DirectionalEac`. CPI is therefore flat past period 15,
and EAC stops moving. That is the honest reading: with no independent spend signal left, *performance
continues as observed* is all the data supports.

The rejected alternative is worth recording. Holding the last projected **increment** as a flat
run-rate is the obvious extrapolation, and it fails badly: the ridge model predicts a non-positive
h=3 increment for some centres, the monotone clamp floors it at zero, and AC then flatlines while EV
keeps climbing. Measured on the workbook fixture, BCC-STR-CON-205 drifted to a CPI of **1.176** and a
VAC of **+104k** by period 21 — a centre appearing to come in six figures *under* budget purely
because the spend model had run out of things to say. Carrying CPI instead holds it at the value the
cone ends on, and the numbers converge (see below).

**2. A centre that finishes stops spending.** Past its projected finish period, AC is frozen at the
value it had there. Without this, EV plateaus at BAC while AC climbs forever and CPI decays on a
centre that is complete.

## The self-consistency check

At a centre's projected finish, EV has reached BAC, so `EAC = BAC × AC / EV` collapses to `AC`. The
cumulative cost-to-complete and the forecast final cost become the same number:

```
BCC-STR-CON-205  (live, GET /api/v1/forecast/panel?period=N&bcc=BCC-STR-CON-205)
 p12 Measured      pct  69.0  EV 483,440  AC 518,106  CPI 0.933  EAC 750,879  VAC  −50,241  AMBER
 p13 Forecast      pct  72.6  EV 508,873  AC 525,186  CPI 0.969  EAC 723,098  VAC  −22,460  GREEN
 p14 Forecast      pct  76.3  EV 534,307  AC 612,871  CPI 0.872  EAC 803,659  VAC −103,021  AMBER
 p15 Forecast      pct  79.9  EV 559,740  AC 701,349  CPI 0.798  EAC 877,893  VAC −177,255  AMBER
 p16 Extrapolated  pct  83.5  EV 585,173  AC 733,216  CPI 0.798  EAC 877,893  VAC −177,255  AMBER
 p18 Extrapolated  pct  90.8  EV 636,039  AC 796,951  CPI 0.798  EAC 877,893  VAC −177,255  AMBER
 p21 Extrapolated  pct 100.0  EV 700,638  AC 877,893  CPI 0.798  EAC 877,893  VAC −177,255  AMBER
```

Period 21: `AC == EAC == 877,893`. If those two ever diverge, the cost-to-complete and the forecast
final cost are no longer the same statement and one of them is wrong —
`ProjectedPanelTests.Past_the_spend_backtest_cost_performance_is_carried_and_lands_on_the_directional_eac`
fails when they do.

Note periods 13→15: CPI sliding 0.969 → 0.872 → 0.798 is the two models disagreeing — work at 3.6
pp/period against spend rising faster — and it flips the centre back to AMBER with a −177k VAC while
the workbook still says −50k. That is the early warning, and it is the reason CPI had to be the ratio
of two independent projections rather than a carried-forward constant.

## What is missing, stated plainly

**There is no PV past period 12, so there is no SPI.** `qs.cost_centre_plan_periods` FKs to
`qs.reporting_periods`, and the importer sets `schedule_horizon_period_id` to the last imported
period — the baseline curve ends where the actuals do. Every projected row returns `pv: null` and
`spi: null` with the reason attached; the UI renders a dash and says why. This is the same ceiling
[feature 16](24-forecast-build-sequence.md) names for its own accuracy: a schedule-integrated version
of this application would have a forward plan curve to compare against. This one does not, and does
not pretend to.

**Variance attribution does not project.** `/api/v1/variance` splits a reported variance across
resource lines; a projected period has none. The drawer says so rather than letting "not diagnosable"
read as a fault.

## Where it lives

| Concern | File |
|---|---|
| The composition | `src/QsEarlyWarning.Core/Forecasting/EvmProjector.cs` |
| Shapes + the licence argument | `src/QsEarlyWarning.Core/Forecasting/ProjectedModels.cs` |
| Endpoint `GET /api/v1/forecast/panel?period=&bcc=` | `src/QsEarlyWarning.Web.API/Controllers/ForecastController.cs` |
| Tests (17) | `tests/QsEarlyWarning.Tests/ProjectedPanelTests.cs` |
| Take-off side panel | `frontend/…/src/components/IfcTakeoff.tsx` |
| Drawer banner | `frontend/…/src/components/CostCentreDetail.tsx` |

## Scope

The take-off tab and the drawer it opens. The header period selector, EVM Overview, Cost Centres and
the 3D Cost X-Ray keep their period-12 ceiling — `/api/v1/overview` and `/api/v1/model/cost-map` still
reject a period the workbook does not reach, deliberately. `/api/v1/forecast/panel` is the one surface
that crosses the boundary, and at or below the origin it returns the reported rows unchanged, so
adopting it elsewhere is a swap rather than a rewrite.
