# Feature 15 — 4D Build Sequence

## What it is

The project's twelve periods of progress, played on the model, with every element coloured by
its cost centre's alert level. Twelve rows of EVM nobody reads twice become a thirty-second
briefing. Shipped both as an in-app control and as a deterministically rendered video,
`presentation/tower-4d-build.mp4`.

## Who it's for

Anyone who has to explain the cost position to someone who will not open the dashboard — a
project director, a client, a review board.

## How it works

Once elements reach cost centres (see [17 — the element register](17-ifc-boq-element-map.md)),
the sheet's own progress curves can drive the model. **▶ Build** on the IFC Take-off tab plays
periods 1 → 12: the structure rises by each centre's `Actual_Pct_Complete` while every element
carries that centre's alert colour.

**The sequence** (`model/ifcSequence.ts`) orders elements **bottom-up by storey** (`Sub Level` →
`01 - Entry Level` → `02` → `03` → `Roof`), then by `GlobalId` for a stable tie-break, and
reveals the first *n* once the centre reaches *n ÷ total*. Two supporting rules, both chosen so
the animation cannot hide trouble:

- An element appears as soon as **any** of its centres reaches it — waiting for every trade would
  make the building lag its own concrete.
- An element shows the **worst** alert among its centres. A slab whose concrete is on budget and
  whose formwork is drifting is a slab with a problem.

**What each frame says, beside the picture.** The model alone shows shape and colour, not scope or
money, so three readouts ride along with it and every one of them is derived from the period being
drawn:

- **Rising now** (top left) — the cost centres that gained ground *this* period, richest work first,
  each with its package code, its percent complete, the points it gained, and a dot in its own alert
  colour. Ranked by `BAC × points gained` rather than by percentage, because the biggest percentage
  move is often the smallest centre. Scoped to the centres the model can actually show: naming a
  rising fit-out package beside a picture in which nothing fit-out can move would describe something
  the viewer cannot see.
- **The two lines under it** exist so that list cannot be mistaken for the job — how many more
  centres moved on the model, how many moved across the whole project, and the share of the bill the
  elements on screen carry (`22.2M of 224.3M AED — 10% of it`).
- **Project to date** (the strip above the legend) — earned value, actual cost and CPI, forecast at
  completion with the overrun named rather than signed, and percent complete against percent
  planned with SPI. Project-wide, labelled as such. These are the *same sums over the same rows*
  the `/api/v1/evm` endpoint performs (`model/buildVideoStats.totalsOf` mirrors
  `DashboardController.Totals`), so a frame paused beside the EVM tab reads identically — period 12
  says EV 77.3M, CPI 0.933, EAC 240.4M, 16.1M over, SPI 0.865 on both.

The period readout also carries the calendar month (`Period 7 · Apr 2026`), parsed out of the date
string rather than through `toLocaleDateString`, which would render differently on a differently
configured machine and break the byte-identical guarantee below.

**The render is not a screen recording.** The app publishes a small purpose-built surface at
`/?render=1` (`components/RenderHarness.tsx`) exposing `window.__qsRender` with `ready` and
`renderFrame(t, camT)`. Its only job is to draw the model and its caption at a fixed size and
resolve when a requested frame is actually on screen. The driver
(`tools/render_build_video/render.mjs`, Node + Chrome DevTools Protocol + ffmpeg, no npm
dependencies) requests each frame, waits for the model to report it has finished redrawing, and
captures. Nothing is paced by a wall clock.

**The camera** (`model/cameraPath.ts`) is a pure function of `t` — a 0.55π azimuthal sweep at a
distance derived from the bounding-sphere radius at 60° FOV, with controls disabled during
render. Frame *k* is therefore identical on every run.

Because the harness is separate from the product UI, renaming a button or moving a panel cannot
change or silently break the video.

**Three surfaces draw a frame; none of them owns its wording.** The harness and the in-app Build
Video panel mount the same `components/RenderOverlay.tsx`, so the previewed frame and the recorded
one cannot drift apart. The third — `model/videoCompositor.ts` — cannot share JSX, because
`captureStream()` sees only the WebGL canvas and would silently drop every word of the overlay; it
redraws the same blocks into a 2D canvas. All three take every string from
`model/buildVideoCopy.ts` and every number from `model/buildVideoStats.ts`, and a test reads the
sources to assert none of them re-types either. That test exists because the drift had already
happened once: the in-app panel had hard-coded the element counts the harness imported.

## Regenerating

```
node tools/render_build_video/render.mjs
```

240 frames at 30fps → 8 seconds at 1600×900, about a minute. Needs the dev server and API up
(`/run_system`). See `.claude/commands/render-video.md`.

## Guarantees & limits

- **Determinism is the contract, and it is testable.** The same data renders byte-identical
  frames every time — verified by rendering twice with `--keep-frames` and diffing the frame
  checksums.
- **The pace and the colour are the workbook's.** Real S-curves — 0% at period 1 rising to 66–77%
  by period 12, per cost centre. GREEN elements appear among the AMBER around period 8 because
  those centres genuinely recovered that month.
- **The order is a declared assumption.** `Actual_Pct_Complete` is per cost centre, never per
  element: a centre at 43% says nothing about *which* 43% of its 299 slabs are poured. Bottom-up
  by storey is what every 4D planning tool does and is defensible for a concrete frame, but it is
  a sequence that was chosen. The on-screen caption says so on every frame — *"The order is
  assumed, the amounts are not."*
- **The scope gap is visible.** The 375 unpriced beams stay grey ghosts for the whole run, because
  no bill item ever paid for them — structure that never fills in.
- **The building does not top out, and that is the data's fault, not a bug.** Period 12 is the end
  of the workbook, not the end of the project: the structure is 66–77% built when the sequence
  stops. [Feature 16](24-forecast-build-sequence.md) projects each centre's progress forward and
  carries the sequence to completion, in visually separate tiers so a projection never reads as a
  measurement.

## A note on headless WebGL

The stripped `chrome-headless-shell` builds bundled with Playwright and Puppeteer crash creating
a WebGL context on this machine; the full Google Chrome app in `--headless=new` gets a real GPU
(ANGLE Metal) with no flags at all. The renderer tries the full Chrome first and falls back to a
known-good headless shell; `--browser` overrides both.
