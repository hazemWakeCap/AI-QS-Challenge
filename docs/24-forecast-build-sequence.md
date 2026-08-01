# Feature 16 — Forecast Build Sequence

## What it is

The 4D build sequence, continued past the last reported period. The workbook ends at period 12
with the modelled structure only 66–77% built, so [feature 15](23-4d-build-sequence.md) stops on
an unfinished building. This projects each cost centre's physical progress forward and keeps the
sequence rising to topping out — **period 21** for Tower X's eight structure centres.

It answers a question a QS asks constantly and the dashboard could not: *at this pace, when does
the frame close, and which trade finishes last?*

## Who it's for

Anyone planning around a completion date rather than reviewing a closed period: the QS agreeing a
programme, the planner sequencing follow-on trades, the commercial manager forecasting when
retention releases.

## The three tiers, and why they look different

A forecast rendered as solid geometry on a 3D model is the most persuasive surface in this whole
application. It is therefore the most dangerous, and the design's first job is to make a projected
element impossible to mistake for a built one.

| Tier | Periods | What stands behind it | How it draws |
|------|---------|----------------------|--------------|
| **Measured** | 1–12 | The workbook | Solid, full opacity |
| **Forecast** | 13–15 | Back-tested: mean error ±1.9 / 3.2 / 3.9 pp | Solid at the pessimistic end, translucent shell to the optimistic |
| **Extrapolated** | 16–21 | Same arithmetic, **no measurement at this range** | Same shell, wider, labelled unvalidated |

The tier is visible in four places at once: the slider track is banded (solid accent → amber →
hollow), a pill beside it names the tier, the readout badge takes the tier's colour and states its
warrant, and the geometry itself carries the band. None of that is decoration.

## The method

**Continue the pace of the last three reported periods.** Per cost centre:

```
projected % at period p  =  clamp( actual%(origin) + pace × (p − origin), 0, 100 )
pace                     =  mean Δ actual% over the ≤3 adjacent periods ending at the origin
```

That is deliberately the simplest thing that works — and "works" is measured, not asserted.
`ProgressBacktest` scores it by rolling origin over the project's own history (origins 4–11, 173
centres, 1,384 / 1,211 / 1,038 scored rows) against three alternatives:

| Predictor | MAE h=1 | h=2 | h=3 |
|---|---|---|---|
| **pace** (deployed) | **1.85 pp** | **3.22 pp** | **3.90 pp** |
| plan-pace × SPI | 1.81 | 3.24 | 3.92 |
| plan-pace | 1.87 | 3.38 | 4.35 |
| hold — assume the work stops | 2.93 | 5.45 | 7.59 |

`pace` wins at two of three horizons and is within 0.04 pp at the third, on a fraction of the
inputs. Every figure the UI quotes is read from this back-test at runtime, never typed into a
caption, so the claim and the measurement cannot drift apart.

### The ceiling this method does not reach, stated plainly

Scaling the plan's **future** increments by SPI scores considerably better — about 1.3 / 1.5 / 1.9
pp. It is not used, because `Plan_Pct_Complete` in `9_HISTORICAL_DATA` ends at the same period the
actuals do: past the origin there is no future plan curve left to scale. A schedule-integrated
version of this feature could reach that accuracy. This one cannot, and says so rather than
implying otherwise.

The same missing plan curve is why [feature 17](25-projected-evm.md) returns no PV and no SPI past
the origin, even though it does project the rest of the EVM row.

## The band

`P10` and `P90` are the median projection offset by the **empirical residual quantiles** the
back-test measured at that horizon — computed at snapshot build, never hard-coded:

| Horizon | P10 | P90 | Width |
|---|---|---|---|
| h=1 | −1.39 pp | +5.07 pp | 6.5 pp |
| h=2 | −2.48 pp | +8.41 pp | 10.9 pp |
| h=3 | −4.05 pp | +10.31 pp | 14.4 pp |

Right-skewed: the method under-predicts on bursts more than it over-predicts on stalls.

On the model the band becomes geometry. Work projected to stand even at `P10` draws solid; work
only `P90` reaches draws translucent. At period 14 that reads *"967 standing, 81 more might be"* —
the interval is in the picture, not in a footnote beside it.

## Five stated assumptions

1. **Pace is clamped at ≥ 0.** Reported progress going backwards is a re-measurement, not
   de-construction. The workbook contains exactly one such reversal (`BCC-COM-SEC-1216`, 0.83% at
   period 10 → 0% at period 11); projecting it forward would delete work that physically exists.
   Such a centre reads **stalled** and is given no finish period at all — the honest answer, not a
   missing value.
2. **The alert level is carried forward from the origin.** No cost verdict exists for a period that
   has not happened. Carrying the last one forward is the same assumption the pace makes, so it
   asserts nothing extra; inventing a "forecast" colour would assert less than the data supports.
   Opacity, not hue, is what says *projected*.
3. **The band past h=3 is scaled, not measured** — the h=3 quantiles widened by √(h/3). Scoring a
   horizon needs a reported period to score it against, and the panel does not reach that far.
   Every point it touches carries the `Extrapolated` tier.
4. **The build order is inherited from feature 15** and remains an assumption: the sheet records
   percent complete per cost centre, never per element, so elements rise bottom-up within their
   trade.
5. **No cost is forecast.** Progress and spend are different forecasts with different error
   structures. Past the origin, CPI/BAC/EV/AC are shown at the last measured period and labelled as
   such. Deriving EV or AC from a projected percentage would manufacture a final-cost number with
   none of the validation such a number needs — see the honesty ledger row on EAC.

## What it produces for Tower X

The register reaches eight structure cost centres. All eight have a positive pace, so all eight
finish inside the horizon:

| Centre | % at P12 | Pace (pp/period) | Tops out |
|---|---|---|---|
| BCC-STR-RBR-214 | 77 | 14.08 | P14 |
| BCC-STR-CON-204 | 75 | 10.58 | P15 |
| BCC-STR-FWK-209 | 74 | 10.30 | P15 |
| BCC-STR-CON-206 | 77 | 6.74 | P16 |
| BCC-STR-FWK-211 | 66 | 8.94 | P16 |
| BCC-STR-RBR-212 | 67 | 5.73 | P18 |
| BCC-STR-FWK-210 | 74 | 4.16 | P19 |
| BCC-STR-CON-205 | 69 | 3.63 | P21 |

Reinforcement finishes first, and `BCC-STR-CON-205` — 3.63 pp/period against its trade's 10.58 —
is the one that holds the frame open for five extra periods. That is the read the feature exists
to produce, and it is invisible in twelve rows of EVM.

Project-wide the picture is different and the API reports it rather than smoothing it: **26 of 173
centres have no pace at all** and are given no finish period. The horizon is capped at 24 periods
past the origin so a centre creeping at 0.3 pp/period cannot request a 297-period timeline.

## How to use it

On the **IFC Take-off** tab: drag the period slider past 12, or press **▶ Build** and let it run
1 → 21. Scrubbing repaints in place; the 8 MB IFC is never re-parsed.

**The slider and ▶ Build render the same frames.** The slider means "what stands at period N" across
the whole range, and playback is an auto-scrub of it. An earlier version had the slider recolour a
permanently-complete building while only ▶ Build made it rise, which produced a visible seam the
moment the projection arrived: stepping from period 12 to 13 took the model from all 1,127 elements
down to 887, so the building shrank while moving forward in time. One meaning for the slider is what
removes that.

The cost of that choice, stated: there is no longer a view of the whole scope coloured at an early
period — at period 5 you see the ~30% that is built. The side panels carry the full-scope cost
picture, which is the better place for it.

## API

```
GET /api/v1/forecast/progress?bcc=BCC-STR-CON-205&through=21
```

Both parameters optional — omit `bcc` for every centre, omit `through` to run to the period the
slowest requested centre tops out. Passing only the centres on screen is what keeps Tower X's
structure horizon at 21 instead of the project-wide 35.

Returns `originPeriod`, `horizonPeriod`, `backtestedThroughPeriod`, `suggestedHorizonPeriod`, the
per-centre point series with `p50Pct` / `p10Pct` / `p90Pct` / `tier`, and the full `validation`
block (per-predictor metrics, bands, notes) that the UI quotes.

## Where it lives

| Concern | File |
|---|---|
| Projection | `Core/Forecasting/ProgressForecaster.cs` |
| Back-test + bands | `Core/Forecasting/ProgressBacktest.cs` |
| Shapes + frozen config | `Core/Forecasting/ProgressModels.cs` |
| Pace helpers | `Core/Forecasting/IncrementHelper.cs` |
| Endpoint | `Web.API/Controllers/ForecastController.cs` (`progress`) |
| Sequence maths | `frontend/src/model/ifcSequence.ts` |
| Shell rendering | `frontend/src/model/ifcPaint.ts` |
| UI | `frontend/src/components/IfcTakeoff.tsx` |
| Tests | `tests/ProgressForecastTests.cs` (19), `frontend/src/model/ifcSequence.test.ts` (14) |

## What is deliberately not built

- **No cost forecast on this surface.** See assumption 5.
- **No horizon past 24 periods.** A backstop against nonsense, not a claim that 24 is meaningful.
- **The exported video still runs 1 → 12.** Extending it was out of scope; the shared sequence
  modules take the projection as an optional argument precisely so the renderer's determinism
  contract is untouched. Verified: two renders on this code are byte-identical to two renders on the
  code before it.
