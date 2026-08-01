import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  api, type CostCentreEvm, type CostMap, type ProjectedCentre, type ProjectedPanel,
  type TakeoffLineRequest, type TakeoffPricing,
} from "../api/client";
import { money, millions, ratio, DASH } from "../format";
import { centreLegend, forecastLegend, hex } from "../model/costPaint";
import { buildCostLinks, TIER_CONFIDENCE, type CostLinkResult } from "../model/ifcCostLink";
import { buildElementIndex, type ElementMapIndex, type ResolvedElement } from "../model/ifcElementMap";
import { fetchBundledIfc, loadIfc } from "../model/ifcLoader";
import { measureModel, type ModelMeasurement } from "../model/ifcMeasure";
import {
  paintIfcByCost, paintIfcByCostCentre, paintSequenceFrame, showAllElements, unplacedLegend,
} from "../model/ifcPaint";
import { elementAtPointer, showSelection } from "../model/ifcPick";
import {
  buildProgressIndex, buildProjectedVerdicts, buildSequence, frameAt, reachOf, tierAt,
  type BuildSequence, type ProgressIndex, type SequenceFrame,
} from "../model/ifcSequence";
import { mapToZones, type ZoneMapResult } from "../model/ifcZoneMap";
import { createViewer, fitToBounds, type Viewer } from "../model/viewer";
import { Spinner } from "./Loading";
import * as THREE from "three";
import * as FRAGS from "@thatopen/fragments";

/**
 * IFC Take-off — measure a real model, price it with this project's rate library, and show where
 * the cost plan would put it.
 *
 * The other 3D tab answers "where is my money in trouble" on a massing we derived from the BOQ.
 * This one starts from the opposite end: here is a real building nobody has priced — what does it
 * cost, can it even be measured, and how firmly does each element bind to a budget?
 *
 * The colours are Tower X's zone cost. The geometry is not Tower X. That gap is the point of the
 * exercise and is stated on the page rather than papered over: what travels between an arbitrary
 * model and a cost plan is the *mechanism*, and the honest measure of it is how much of the model
 * the mechanism can place, at what confidence.
 */
/** Share of a total, as a whole-number percentage. Returns a dash when there is nothing to divide. */
const pct = (n: number, total: number) => (total > 0 ? `${((100 * n) / total).toFixed(0)}%` : DASH);

/** A measured quantity, to one decimal — the precision a take-off is actually good to. */
const qty = (n: number) => n.toLocaleString(undefined, { maximumFractionDigits: 1 });

/** Where a period sits along a slider track, as a percentage — used to band the track by tier. */
const pctOf = (value: number, min: number, max: number) =>
  max > min ? (100 * (value - min)) / (max - min) : 100;

/**
 * The four questions the evidence panels answer, one shown at a time.
 *
 * Stacked, they ran to 4,600px — seven screens of scrolling, and the model itself left the viewport
 * after the first one, which is the opposite of what a 3D tab is for. They are grouped rather than
 * merely collapsed because they are not one list: each is a different question about whether the
 * take-off can be trusted, and a QS asks one of them at a time.
 *
 * Every tab carries its own headline figure, so the numbers that matter stay legible without
 * switching. Hiding "375 unpriced" behind a click would be exactly the kind of quiet omission this
 * tab exists to prevent.
 */
type SidePanel = "priced" | "bill" | "measurable" | "plan";

export function IfcTakeoff({
  period,
  onSelectCentre,
}: {
  period: number;
  /** Hands a cost centre to the app's shared drawer. The period travels with it: this tab scrubs
   *  independently, so a drawer opened from period 6 must not show period 12 numbers — and one
   *  opened from period 16 must not either. */
  onSelectCentre?: (centre: ProjectedCentre, period: number) => void;
}) {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const viewerRef = useRef<Viewer | null>(null);
  /** The loaded model, held so a repaint never has to re-parse 8 MB of IFC. */
  const modelRef = useRef<FRAGS.FragmentsModel | null>(null);
  /** Live index + selection, read from inside the pointer handler installed once per viewer. */
  const indexRef = useRef<ElementMapIndex | null>(null);
  const selectedRef = useRef<number | null>(null);

  const [status, setStatus] = useState<string>("Starting viewer…");
  const [busy, setBusy] = useState(true);
  const [err, setErr] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string>("school_str.ifc");
  const [measurement, setMeasurement] = useState<ModelMeasurement | null>(null);
  const [pricing, setPricing] = useState<TakeoffPricing | null>(null);
  const [zoneMap, setZoneMap] = useState<ZoneMapResult | null>(null);
  const [links, setLinks] = useState<CostLinkResult | null>(null);
  const [index, setIndex] = useState<ElementMapIndex | null>(null);
  /**
   * Every period's EVM panel — reported rows at or below the origin, projected past it.
   *
   * Fetched once for the whole timeline rather than per scrub position. Fetching on demand meant the
   * figures arrived a round trip after the clock moved, so during ▶ Build the panel was permanently
   * one period behind: the heading read "period 13" above period 12's CPI, with no projected caveat
   * because a period-12 row is measured. Holding the series makes every read synchronous, which is the
   * only way the heading and the figures can be guaranteed to name the same period. The whole set
   * costs one parallel burst of ~25ms calls, and the measured half is a server-side passthrough.
   */
  const [panelByPeriod, setPanelByPeriod] = useState<Map<number, ProjectedPanel> | null>(null);
  /** Every cost-centre code in the project, for element-name matching. Period-invariant, so fetched
   *  once and held rather than re-read on every scrub and every playback step. */
  const centreCodesRef = useRef<string[] | null>(null);
  const [selected, setSelected] = useState<ResolvedElement | null>(null);

  // ── 4D playback ──
  // `playT` is a fractional period so the building rises continuously rather than jumping monthly.
  const [sequence, setSequence] = useState<BuildSequence | null>(null);
  const [centresByPeriod, setCentresByPeriod] = useState<Map<number, CostCentreEvm[]> | null>(null);
  const [playT, setPlayT] = useState<number | null>(null);
  const [playing, setPlaying] = useState(false);
  /**
   * The frame currently on screen.
   *
   * Held as state rather than only in `prevFrameRef` because the side panel reads from it: the
   * selected element's colour has to be quoted from the frame that actually painted it, not
   * recomputed from the period and hoped to match.
   */
  const [frame, setFrame] = useState<SequenceFrame | null>(null);
  /**
   * Projected percent complete past the last reported period, so the sequence can run to topping out.
   *
   * Null when the endpoint is unavailable, and everything below falls back to the measured range —
   * the tab worked without a projection before this existed and must keep working without one.
   */
  const [progress, setProgress] = useState<ProgressIndex | null>(null);
  /** Whether the projection has answered yet — null progress means "none available", not "not asked". */
  const [progressResolved, setProgressResolved] = useState(false);
  /** Last drawn frame, so each tick only applies what actually changed. */
  const prevFrameRef = useRef<SequenceFrame | null>(null);
  /** Serialises frame paints — see the draw effect for why this cannot be fire-and-forget. */
  const paintChainRef = useRef<Promise<void>>(Promise.resolve());
  // Tower X's cost map, used only to report which of ITS zones this model reaches — never to
  // suggest the loaded building shares that budget.
  const [costMap, setCostMap] = useState<CostMap | null>(null);
  const [showRules, setShowRules] = useState(false);
  const [sidePanel, setSidePanel] = useState<SidePanel>("priced");

  /** Period shown in this tab. Seeded from the app selector, then scrubbable in place. */
  const [viewPeriod, setViewPeriod] = useState(period);
  useEffect(() => {
    setViewPeriod(period);
    // Leaving playback, not just re-seeding. The playback clock outranks the scrub position
    // everywhere it is read, and nothing used to clear it — so once ▶ Build had run, the app's period
    // selector was silently inert: the header could read "period 3" while the tab sat at topping out.
    // Picking a period from the selector is an explicit instruction, and it wins.
    setPlayT(null);
    setPlaying(false);
  }, [period]);

  /** Load → measure → price. */
  const ingest = useCallback(async (bytes: Uint8Array, name: string) => {
    const viewer = viewerRef.current;
    if (!viewer) return;

    setBusy(true);
    setErr(null);
    setMeasurement(null);
    setPricing(null);
    setZoneMap(null);
    setLinks(null);
    setIndex(null);
    setSelected(null);
    // A new model binds a different set of centres, so the projection and every period's figures are
    // re-derived rather than carried over from the file that was open before.
    setProgress(null);
    setProgressResolved(false);
    setPanelByPeriod(null);
    indexRef.current = null;
    selectedRef.current = null;
    setFileName(name);

    try {
      setStatus("Converting IFC…");
      const model = await loadIfc(viewer, bytes, (p) =>
        setStatus(`Converting IFC… ${Math.round(p * 100)}%`),
      );
      modelRef.current = model;

      const boxes = await model.getBoxes();
      if (boxes?.length) {
        const merged = new THREE.Box3();
        for (const b of boxes) merged.union(b);
        await fitToBounds(viewer, merged);
      }

      setStatus("Measuring elements…");
      const measured = await measureModel(model);
      setMeasurement(measured);

      setStatus("Pricing against the rate library…");
      const lines: TakeoffLineRequest[] = [];
      for (const c of measured.byClass) {
        lines.push({
          ifcClass: c.ifcClass,
          measure: "volume",
          quantity: c.volume,
          elementCount: c.volumeCount > 0 ? c.elementCount : 0,
          unmeasuredCount: c.volumeCount > 0 ? 0 : c.elementCount,
        });
        // Area rides alongside volume for the same elements, so it contributes no element count —
        // counting them twice would break the tie-out against the model's real element total.
        if (c.area > 0) {
          lines.push({
            ifcClass: c.ifcClass, measure: "area", quantity: c.area,
            elementCount: 0, unmeasuredCount: 0,
          });
        }
      }

      setPricing(await api.priceTakeoff(lines, measured.report.totalElements));

      // Zone classification, linking and paint all depend on the cost map at the SELECTED period,
      // so they live in their own effect keyed on `measurement`. Doing them here would make
      // `ingest` — a dependency of the viewer effect — depend on period state, which would tear the
      // viewer down and re-parse the 8 MB IFC on every scrub.
      setStatus("");
    } catch (e) {
      setErr(String((e as Error).message ?? e));
    } finally {
      setBusy(false);
    }
  }, []);

  // ── viewer lifecycle + first load ──
  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    let cancelled = false;
    let owned: Viewer | null = null;

    // Same drag guard as the massing tab: a pointer that moved more than 4px was orbiting the
    // camera, not selecting. Without it every rotation reselects whatever ends up under the cursor.
    let down: { x: number; y: number } | null = null;
    const onPointerDown = (e: PointerEvent) => { down = { x: e.clientX, y: e.clientY }; };
    const onPointerUp = (e: PointerEvent) => {
      if (!down) return;
      const moved = Math.hypot(e.clientX - down.x, e.clientY - down.y);
      down = null;
      if (moved > 4) return;

      const canvas = host.querySelector("canvas");
      const viewer = viewerRef.current;
      const model = modelRef.current;
      if (!canvas || !viewer || !model) return;

      void (async () => {
        const localId = await elementAtPointer(viewer, e, canvas);
        // Clicking the same element again clears it, matching the massing tab's zone toggle.
        const next = localId !== null && localId === selectedRef.current ? null : localId;
        await showSelection(viewer, model, next, selectedRef.current);
        selectedRef.current = next;
        setSelected(next === null ? null : indexRef.current?.byLocalId.get(next) ?? null);
      })();
    };

    (async () => {
      try {
        const viewer = await createViewer(host);
        if (cancelled) {
          viewer.dispose();
          return;
        }
        owned = viewer;
        viewerRef.current = viewer;

        host.addEventListener("pointerdown", onPointerDown);
        host.addEventListener("pointerup", onPointerUp);

        setStatus("Fetching bundled model…");
        const bytes = await fetchBundledIfc();
        if (cancelled) return;
        await ingest(bytes, "school_str.ifc");
      } catch (e) {
        if (!cancelled) {
          setErr(String((e as Error).message ?? e));
          setBusy(false);
        }
      }
    })();

    return () => {
      cancelled = true;
      host.removeEventListener("pointerdown", onPointerDown);
      host.removeEventListener("pointerup", onPointerUp);
      viewerRef.current = null;
      modelRef.current = null;
      indexRef.current = null;
      selectedRef.current = null;
      owned?.dispose();
      host.querySelectorAll("canvas").forEach((c) => c.remove());
    };
  }, [ingest]);

  // ── resolve the authored register against this model ──
  // Static, so it is fetched once per loaded model rather than on every period scrub.
  useEffect(() => {
    const model = modelRef.current;
    if (!model || !measurement) return;

    let cancelled = false;
    (async () => {
      try {
        const map = await api.elementMap();
        if (cancelled) return;
        const built = await buildElementIndex(model, map);
        if (cancelled) return;
        indexRef.current = built;
        setIndex(built);
      } catch {
        // No register for this project, or it does not resolve against this model. The tab falls
        // back to zone placement, which is what it did before the register existed.
        indexRef.current = null;
        setIndex(null);
      }
    })();

    return () => { cancelled = true; };
  }, [measurement]);

  /**
   * The cost centres this model actually reaches.
   *
   * Asking for all 173 would stretch the horizon to whichever centre is slowest project-wide — the
   * eight structure centres the register binds top out inside nine periods, and that is the timeline
   * worth showing here. Hoisted out of the sequence effect because the projected panel has to request
   * the same set: two callers asking for different centres would get two different horizons, and the
   * slider would then run past the periods the side panel has figures for.
   */
  const mappedBccIds = useMemo(() => index ? [...new Set(
    index.mappedLocalIds
      .flatMap((id) => index.byLocalId.get(id)?.items ?? [])
      .map((i) => i.bccId)
      .filter((b): b is string => Boolean(b)),
  )] : [], [index]);

  // ── 4D: the build order, and every period's progress ──
  // Fetched once per model. The sequence is deterministic, so a video rendered twice is identical.
  useEffect(() => {
    if (!index || !costMap) return;

    let cancelled = false;
    (async () => {
      const periods: number[] = [];
      for (let p = costMap.minPeriod; p <= costMap.maxPeriod; p++) periods.push(p);

      const rows = await Promise.all(
        periods.map((p): Promise<[number, CostCentreEvm[]]> =>
          api.costCentres(p).then((r) => [p, r] as [number, CostCentreEvm[]])
            .catch(() => [p, []] as [number, CostCentreEvm[]])),
      );
      if (cancelled) return;

      setCentresByPeriod(new Map(rows));
      setSequence(buildSequence(index));

      try {
        const f = await api.progressForecast(mappedBccIds);
        if (!cancelled) setProgress(buildProgressIndex(f));
      } catch {
        // No projection for this project. The sequence still plays the reported periods.
        if (!cancelled) setProgress(null);
      }
      // Settled either way — the prefetch below waits on this rather than on `progress` being
      // non-null, because "no projection" is a real answer and must not stall the figures forever.
      if (!cancelled) setProgressResolved(true);
    })();

    return () => { cancelled = true; };
  }, [index, mappedBccIds, costMap?.minPeriod, costMap?.maxPeriod]);

  // ── every period's figures, up front ──
  //
  // Runs once the timeline's extent is known — which needs the projection, because the horizon is
  // whatever it says rather than the last reported period. Periods that fail are simply absent from
  // the map and the panel shows nothing for them, exactly as it does before the fetch lands.
  const firstPeriod = costMap?.minPeriod;
  const lastReportedPeriod = costMap?.maxPeriod;
  useEffect(() => {
    if (!mappedBccIds.length || firstPeriod === undefined || lastReportedPeriod === undefined) return;
    if (!progressResolved) return;
    const last = progress?.horizonPeriod ?? lastReportedPeriod;

    let cancelled = false;
    (async () => {
      const periods: number[] = [];
      for (let p = firstPeriod; p <= last; p++) periods.push(p);

      const entries = await Promise.all(periods.map((p) =>
        api.projectedPanel(p, mappedBccIds)
          .then((r) => [p, r] as const)
          .catch(() => null)));
      if (cancelled) return;

      setPanelByPeriod(new Map(entries.filter((e): e is readonly [number, ProjectedPanel] => e !== null)));
    })();

    return () => { cancelled = true; };
    // Keyed on the timeline's *bounds*, never on `costMap` itself. The zone register is re-read each
    // time the clock crosses a period, and each response is a fresh object — depending on it re-ran
    // this whole prefetch on every step of a build, 21 calls at a time.
  }, [mappedBccIds, progressResolved, progress?.horizonPeriod, firstPeriod, lastReportedPeriod]);

  // Advance the clock while playing.
  //
  // Keyed on `playing` alone and reading the ceiling from a ref: an earlier version listed derived
  // values in the dependency array, and every re-render that changed one of them tore the interval
  // down mid-run, so playback stalled a few frames in and looked like a performance problem rather
  // than a lifecycle one.
  const maxPeriodRef = useRef(12);
  useEffect(() => {
    // The projection's horizon when there is one, so playback runs past the last reported period all
    // the way to topping out; the reported range when there is not.
    if (progress) maxPeriodRef.current = progress.horizonPeriod;
    else if (costMap) maxPeriodRef.current = costMap.maxPeriod;
  }, [costMap, progress]);

  useEffect(() => {
    if (!playing) return;
    const id = window.setInterval(() => {
      setPlayT((t) => {
        if (t === null) return t;
        const next = +(t + 0.25).toFixed(2);
        if (next >= maxPeriodRef.current) return maxPeriodRef.current;
        return next;
      });
    }, 120);
    return () => window.clearInterval(id);
  }, [playing]);

  // Stop at the end. Done as an effect rather than inside the state updater, which must stay pure.
  useEffect(() => {
    if (playing && playT !== null && playT >= maxPeriodRef.current) setPlaying(false);
  }, [playing, playT]);

  /**
   * The period the model is drawn at, and the one thing the slider means.
   *
   * <b>The slider and ▶ Build render identically.</b> Both ask "what stands at period N, and what
   * does it read as" — playback is an auto-scrub of the same frames, nothing more. An earlier version
   * had the slider recolour a permanently-complete building while only ▶ Build made it rise, and the
   * seam showed the moment the projection arrived: stepping from period 12 to 13 took the model from
   * all 1,127 elements down to 887, so the building shrank while moving forward in time. One meaning
   * for the slider is what removes that.
   *
   * The cost of it, stated: there is no longer a view of the whole scope coloured at an early period.
   * At period 5 you see the ~30% that is built, not the full school. The side panels carry the
   * full-scope cost picture, which is the better place for it — a table can show you all 173 centres
   * at once and a building cannot.
   */
  const sequenceReady = Boolean(index && sequence && centresByPeriod);
  const drawT = playT ?? (sequenceReady ? viewPeriod : null);

  /** Slider ceiling: the projection's horizon when there is one, else the last reported period. */
  const sliderMax = progress?.horizonPeriod ?? costMap?.maxPeriod ?? 12;

  /**
   * The period every cost figure on this tab is read at — the clock position, unclamped.
   *
   * This used to be held at the origin, on the grounds that a projected percentage is not grounds for
   * inventing a cost. The narrower version of that rule is the one that actually holds: EV is
   * *defined* by the schema as BAC × percent complete, so projecting the percentage projects the
   * earned value by the database's own arithmetic. What may not be derived from progress is spend —
   * and it is not: AC comes from the incremental-spend cone, or the row reports it unavailable and
   * CPI, EAC and VAC go with it. PV and SPI stay null past the origin, because the baseline curve
   * genuinely ends there. See EvmProjector on the server for the whole argument.
   *
   * <b>It reads `playT` too, and must.</b> An earlier version read only the scrub position, so ▶ Build
   * advanced the model, the header and the tier pill while the cost figures sat at wherever the slider
   * had been left. Playing from period 5 to topping out ended with the panel attributing an element to
   * a centre "at period 21" beside that centre's period-5 CPI — and with no projected-basis caveat
   * rendered, because a period-5 row *is* measured. Rounded, so a 0.25-step playback asks the server
   * once per whole period rather than four times.
   */
  /**
   * The whole period the clock is standing in — the single answer to "which period is this?".
   *
   * There were three answers before: `Math.round` for the tier pill, `Math.floor` for the element
   * attribution, and the raw scrub for the figures. At a fractional playback position they disagreed,
   * so at clock 16.5 the panel attributed an element "at period 16" beside cost figures for 17.
   * Rounding is the convention because it matches where the sequence flips from measured to projected
   * geometry; what matters more is that there is only one of it.
   */
  const clockPeriod = Math.round(playT ?? viewPeriod);

  const dataPeriod = clockPeriod;

  /** The figures on screen. A synchronous lookup, so they can never name a different period than the
   *  heading above them; null only before the prefetch lands. */
  const panel = panelByPeriod?.get(dataPeriod) ?? null;
  const centres = panel?.centres ?? [];

  /**
   * What colours the model past the origin — the panel's own verdict, period by period.
   *
   * The whole prefetch rather than `panel` alone, because a frame straddling two periods reads the
   * nearer one and playback crosses a boundary every four ticks. Indexing it once is what keeps the
   * paint from asking a second opinion: before this, the model took its colour from the progress
   * forecaster's origin alert and held it flat for the entire projection, so a centre that had
   * recovered — BCC-STR-CON-205 at CPI 0.933 in period 12, 0.969 in period 13 — went on being drawn
   * as drifting beside a KPI panel reading GREEN off the projected CPI. One source, one verdict.
   */
  const verdicts = useMemo(
    () => (panelByPeriod ? buildProjectedVerdicts(panelByPeriod) : null),
    [panelByPeriod],
  );

  /**
   * The period the *zone* register is read at, held at the origin.
   *
   * Separate from `dataPeriod` on purpose. `/api/v1/model/cost-map` rejects a period the workbook does
   * not reach, and this response carries three things the tab cannot lose: the zone list the model is
   * painted against, and `minPeriod`/`maxPeriod`, which set the slider's own bounds. Letting the scrub
   * drive it would collapse the slider the moment you scrubbed past 12. The zone list is the cost
   * plan's spatial register anyway — it does not move with time.
   */
  const zonePeriod = progress ? Math.min(dataPeriod, progress.originPeriod) : dataPeriod;

  useEffect(() => {
    const viewer = viewerRef.current;
    const model = modelRef.current;
    if (!viewer || !model || !index || !sequence || !centresByPeriod || drawT === null) return;

    const frame = frameAt(drawT, sequence, centresByPeriod, progress, verdicts);
    setFrame(frame);

    // Serialised on purpose. An earlier version fired the paint and advanced `prevFrameRef`
    // immediately, so if a paint was still in flight the next frame's delta was computed against a
    // frame that had never landed — and the elements in between were dropped silently. Frames are
    // now chained, and a frame that arrives while one is painting supersedes rather than races.
    paintChainRef.current = paintChainRef.current
      .then(async () => {
        const previous = prevFrameRef.current;
        await paintSequenceFrame(viewer, model, index, frame, previous);
        prevFrameRef.current = frame;
      })
      .catch(() => { /* a dropped frame must not poison the chain */ });
  }, [drawT, sequence, centresByPeriod, index, progress, verdicts]);

  // Losing the sequence restores the model, so a reload cannot strand the building half-built under
  // a zone paint that knows nothing about which elements the last frame had hidden.
  useEffect(() => {
    if (drawT !== null) return;
    const viewer = viewerRef.current;
    const model = modelRef.current;
    if (!viewer || !model || !index || prevFrameRef.current === null) return;
    prevFrameRef.current = null;
    void showAllElements(viewer, model, index);
  }, [drawT, index]);

  // ── locate in the cost plan ──
  //
  // Deliberately separate from painting. The side panels — the selected element's CPI and EV/AC, the
  // zone match rate, the measurability report — are read from this data, so it has to follow the
  // scrub whether or not the sequence painter is the one drawing. Folding the two together meant
  // that once the sequence took over the model, every figure on the right froze at whatever period
  // happened to be showing when it did.
  useEffect(() => {
    const model = modelRef.current;
    if (!model || !measurement) return;

    let cancelled = false;

    (async () => {
      try {
        // Two calls, two lifetimes. The zone register is read at the origin — it would 400 past it —
        // and follows the clock only because its per-zone costs do. The full centre-code list feeds
        // nothing but name matching, and the *set* of centres is identical in every period, so it is
        // fetched once per model and held; re-reading it on every playback step was 20 identical round
        // trips per build. The EVM figures are not here at all — they are prefetched for the whole
        // timeline above, so that scrubbing and playback read them without waiting.
        const [cm, all] = await Promise.all([
          api.costMap(zonePeriod).catch(() => null),
          centreCodesRef.current
            ? Promise.resolve(centreCodesRef.current)
            : api.costCentres().then((r) => {
                centreCodesRef.current = r.flatMap((c) => [c.bccId, c.packageCode]).filter(Boolean);
                return centreCodesRef.current;
              }).catch(() => [] as string[]),
        ]);
        if (cancelled) return;

        setCostMap(cm);
        const zones = cm?.zones ?? [];

        const zm = mapToZones(
          measurement.byClass,
          measurement.report.storeys,
          zones.map((z) => z.zoneCode),
        );
        if (cancelled) return;
        setZoneMap(zm);

        setLinks(buildCostLinks(measurement, zm, {
          zoneCodes: zones.map((z) => z.zoneCode),
          // Both identifiers a cost centre is known by — an element naming either one was authored
          // with this cost plan in view, even when it names no zone we could paint it into.
          centreCodes: all,
        }));
      } catch (e) {
        if (!cancelled) setErr(String((e as Error).message ?? e));
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [measurement, zonePeriod]);

  // ── paint, when the sequence is not the one doing it ──
  //
  // The degradation path. With a register resolved the sequence painter owns the model at every
  // period, so this runs only before the register lands, or if it never does — in which case there
  // is no per-element progress to sequence and colouring the whole model by zone is the best the
  // data supports.
  useEffect(() => {
    const viewer = viewerRef.current;
    const model = modelRef.current;
    if (!viewer || !model || !zoneMap || !links || drawT !== null) return;

    let cancelled = false;

    (async () => {
      try {
        if (index) {
          await paintIfcByCostCentre(viewer, model, index, centres);
        } else {
          await paintIfcByCost(viewer, model, zoneMap, costMap?.zones ?? [], {
            tierByLocalId: links.tierByLocalId,
          });
        }

        // A repaint overwrites every colour, so the selection marker has to be laid back on top.
        if (!cancelled && selectedRef.current !== null) {
          await showSelection(viewer, model, selectedRef.current, null);
        }
      } catch (e) {
        if (!cancelled) setErr(String((e as Error).message ?? e));
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [zoneMap, links, centres, costMap, index, drawT]);

  const report = measurement?.report;
  const coverage = report && report.totalElements > 0
    ? (100 * report.measuredElements) / report.totalElements
    : null;

  /** What the scrub position currently means. Drives the pill, the badge and the legend. */
  const scrubTier = tierAt(clockPeriod, progress);

  /**
   * Why the selected element is the colour it is.
   *
   * An element is coloured by the worst of the centres that have <i>reached</i> it, and the two are
   * not the same list: at period 8, 151 of 299 slabs are poured and only 106 of them struck, so
   * slabs 107–151 carry an AMBER formwork centre that has not got to them and are painted by the
   * GREEN concrete centre alone. Without this the panel listed both centres, said nothing about
   * which was on screen, and a green centre sat beside an amber element with no way to reconcile
   * them.
   *
   * Computed on selection, not per frame — see `reachOf`.
   */
  const reach = useMemo(() => {
    if (!selected || !sequence || !centresByPeriod || drawT === null) return null;
    return reachOf(selected.localId, drawT, sequence, centresByPeriod, progress, verdicts);
  }, [selected, sequence, centresByPeriod, drawT, progress, verdicts]);

  const reachByBcc = useMemo(
    () => new Map((reach ?? []).map((r) => [r.bccId, r])),
    [reach],
  );
  /** The centre the colour was actually taken from, if any centre has reached this element yet. */
  const driver = reach?.find((r) => r.driving) ?? null;
  /**
   * The error bar the UI is entitled to quote at the horizon being shown — read off the back-test the
   * projection shipped with, never written into the caption by hand, so the two cannot drift apart.
   */
  const shownMae = (() => {
    if (!progress || scrubTier === "measured") return null;
    const h = clockPeriod - progress.originPeriod;
    return progress.accuracy.find((m) => m.predictor === "pace" && m.horizon === h)?.maePp ?? null;
  })();
  /**
   * The evidence tabs, each labelled with the figure it is really about.
   *
   * The hint is the point: a tab reading "Priced · 375 unpriced" tells a QS there is scope with no
   * money behind it before they click anything, so grouping the panels costs no visibility of the
   * numbers that matter.
   */
  const evidencePanels: { key: SidePanel; label: string; hint: string | null }[] = [
    {
      key: "priced",
      label: "Priced",
      hint: pricing ? `${pricing.unpricedElements} unpriced` : null,
    },
    {
      key: "bill",
      label: "Bill check",
      hint: pricing
        ? `${pricing.quantityVariances.length + pricing.uncomparableQuantities.length} lines`
        : null,
    },
    {
      key: "measurable",
      label: "Measurable",
      hint: coverage == null ? null : `${coverage.toFixed(0)}%`,
    },
    {
      key: "plan",
      label: "Cost plan",
      hint: zoneMap ? `${(zoneMap.matchRate * 100).toFixed(0)}% placed` : null,
    },
  ];

  /** Longest measured horizon, for the extrapolated caption's "no measurement past here" claim. */
  const lastMeasuredHorizon = progress
    ? Math.max(0, ...progress.accuracy.filter((m) => m.predictor === "pace").map((m) => m.horizon))
    : 0;

  return (
    <div className="modelview">
      <div className="card modelview-stage">
        <div className="panel-head">
          <span className="muted small mono">{fileName}</span>

          {costMap && (
            <div className="scrub">
              <label htmlFor="takeoff-period" className="muted small">Period</label>
              {/* While a build sequence is loaded the slider scrubs the sequence itself, in quarter
                  periods, so the construction can be stepped by hand as well as played.

                  The track is banded: solid where the workbook reports, hatched where the projection
                  carries a measured error bar, faint past that. The scrub position tells a QS which
                  kind of number they are looking at before they read a word of the caption. */}
              <input
                id="takeoff-period"
                className={progress ? "scrub-forecast" : undefined}
                style={progress ? {
                  // Fractions of the track where each tier ends.
                  "--measured-end": `${pctOf(progress.originPeriod, costMap.minPeriod, sliderMax)}%`,
                  "--forecast-end": `${pctOf(Math.min(progress.backtestedThroughPeriod, sliderMax), costMap.minPeriod, sliderMax)}%`,
                } as React.CSSProperties : undefined}
                type="range"
                min={costMap.minPeriod}
                max={sliderMax}
                step={playT !== null ? 0.25 : 1}
                value={playT ?? viewPeriod}
                onChange={(e) => {
                  const v = Number(e.target.value);
                  if (playT !== null) {
                    setPlaying(false);
                    setPlayT(v);
                  } else {
                    setViewPeriod(v);
                  }
                }}
                aria-label={`Period ${playT ?? viewPeriod}${scrubTier === "measured" ? " (reported)" : scrubTier === "forecast" ? " (forecast)" : " (extrapolated)"}`}
              />
              <span className="mono small">{playT ?? viewPeriod}</span>
              {scrubTier !== "measured" && (
                <span className={`pill pill-sm ${scrubTier === "forecast" ? "pill-warn" : "pill-muted"}`}>
                  {scrubTier === "forecast" ? "forecast" : "extrapolated"}
                </span>
              )}
            </div>
          )}

          {sequence && centresByPeriod && costMap && (
            <div className="seg">
              <button
                className={`btn btn-sm ${playT !== null ? "btn-primary" : "btn-ghost"}`}
                onClick={() => {
                  if (playT === null) {
                    prevFrameRef.current = null; // first frame hides everything, then builds up
                    // Full geometry for everything visible: with the default LOD mode the detail
                    // level depends on camera distance, so elements get reprocessed as the view
                    // shifts. Set once per run — it restarts the model sweep itself.
                    void modelRef.current?.setLodMode(FRAGS.LodMode.ALL_GEOMETRY);
                    setPlayT(costMap.minPeriod);
                    setPlaying(true);
                  } else {
                    setPlaying((p) => !p);
                  }
                }}
              >
                {playT === null ? "▶ Build" : playing ? "❚❚ Pause" : "▶ Resume"}
              </button>
              {playT !== null && (
                <button
                  className="btn btn-sm btn-ghost"
                  // Hands the slider back to the period the tab was scrubbed to. It no longer restores
                  // the whole model, because the slider draws the same sequence playback does — there
                  // is nothing to restore it from.
                  onClick={() => {
                    setPlaying(false);
                    setPlayT(null);
                  }}
                >
                  Stop
                </button>
              )}
            </div>
          )}
        </div>

        <div className="model-canvas" ref={hostRef} role="img" aria-label="Loaded IFC model">
          {busy && (
            <div className="model-loading">
              <Spinner />
              <p className="muted small">{status}</p>
            </div>
          )}
        </div>

        {/* One line of numbers, with the caveat behind a disclosure.
            The caveat has to stay reachable — it is the difference between a forecast and a promise —
            but as four lines of standing prose it was costing the model 100px and getting skipped
            anyway. The summary states which kind of number is on screen; the detail says why. */}
        {drawT !== null && (
          <details className="readout" data-sequence-readout data-tier={scrubTier}>
            <summary>
              <b>Period {clockPeriod}</b> · {(frame?.builtCount ?? 0).toLocaleString()} of{" "}
              {index?.mappedLocalIds.length.toLocaleString()} standing
              {(frame?.shellCount ?? 0) > 0 && (
                <> · {frame!.shellCount.toLocaleString()} might be</>
              )}
              {scrubTier === "measured" && <> · <b>reported</b></>}
              {scrubTier === "forecast" && (
                <> · <b>forecast</b>{shownMae !== null && <> ±{shownMae.toFixed(1)} pp</>}</>
              )}
              {scrubTier === "extrapolated" && <> · <b>extrapolated</b>, no error bar</>}
            </summary>
            {scrubTier === "measured" ? (
              <p>
                <b>The order is assumed, the amounts are not:</b> the sheet records percent complete
                per cost centre, never per element, so elements rise bottom-up within their trade
                while the pace and the colour come from the workbook.
              </p>
            ) : scrubTier === "forecast" ? (
              <p>
                <b>Forecast, not reported.</b> The workbook ends at period {progress?.originPeriod};
                this is each centre&apos;s {progress?.method} carried forward. Back-tested on this
                project&apos;s own history at this horizon:{" "}
                {shownMae !== null ? <>mean error <b>±{shownMae.toFixed(1)} pp</b> of progress</> : "measured"}.
                Solid work is projected to stand even at the pessimistic end; translucent work is
                inside the band and may not be there by then.
              </p>
            ) : (
              <p>
                <b>Extrapolated — no error bar earned here.</b> Same arithmetic as the forecast, but
                the workbook is only long enough to measure accuracy{" "}
                {lastMeasuredHorizon > 0 && <>{lastMeasuredHorizon} periods</>} past period{" "}
                {progress?.originPeriod}, so nothing validates this distance out. Read it as
                &ldquo;where this pace leads&rdquo;, not as a date.
              </p>
            )}
          </details>
        )}

        {zoneMap && (
          <div className="model-legend">
            {/* The register paints each element by its own cost centre, so the key is the centre's
                alert level rather than a zone rollup. */}
            {centreLegend().map((l) => (
              <span key={l.label} className="legend-item" title={l.note}>
                <i style={{ background: hex(l.color) }} aria-hidden="true" />
                {l.label}
              </span>
            ))}
            <span className="legend-item" title={unplacedLegend.note}>
              <i style={{ background: hex(unplacedLegend.color) }} aria-hidden="true" />
              {unplacedLegend.label}
            </span>
            {/* Only while a projection is on screen: the opacity scale means nothing on a reported
                period, where every standing element is equally solid. */}
            {scrubTier !== "measured" && forecastLegend().map((l) => (
              <span key={l.label} className="legend-item" title={l.note}>
                <i style={{ background: hex(l.color), opacity: l.opacity }} aria-hidden="true" />
                {l.label}
              </span>
            ))}
          </div>
        )}

        {/* The claim stays on screen at all times; the paragraph explaining it does not need to.
            A one-line caveat gets read, and the full version is one click away — which is a better
            trade than 130px of standing prose that a reader's eye learns to skip. */}
        <details className="readout readout-caveat">
          <summary>
            <strong>This is not Tower X</strong> — a school model priced at Tower X&apos;s rates
          </summary>
          <p>
            It is a school&apos;s structural model (Autodesk Revit sample, IFC4) being priced with{" "}
            <strong>Tower X&apos;s rate library</strong> and coloured with{" "}
            <strong>Tower X&apos;s zone cost</strong>. The two buildings are unrelated — what is
            being demonstrated is that a rate library and a cost plan travel to any model you can
            measure. A colour here means &ldquo;an element of this kind maps to a zone in that
            state&rdquo;, never that this building holds that budget.{" "}
            <button className="btn btn-sm btn-ghost" onClick={() => setShowRules((s) => !s)}>
              {showRules ? "Hide pricing rules" : "Show pricing rules"}
            </button>
          </p>
        </details>

        {showRules && pricing && (
          <>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>IFC class</th>
                    <th>Measure</th>
                    <th>BOQ item</th>
                    <th>Why this pairing</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.rulesApplied.map((r) => (
                    <tr key={`${r.ifcClass}-${r.measure}`}>
                      <td className="mono">{r.ifcClass}</td>
                      <td>{r.measure} ({r.unit})</td>
                      <td className="mono">{r.boqItemRef}</td>
                      <td className="muted small">{r.rationale}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="muted small">{pricing.rateBasis}</p>
          </>
        )}
      </div>

      <div className="modelview-side">
        {err && <div className="error">{err}</div>}

        {index && (
          <div className="card">
            <h3>Selected element</h3>
            {!selected && (
              <p className="muted small">
                Click any element in the model. {index.map.mappedElements} of{" "}
                {index.map.totalElements} carry a binding to the bill.
              </p>
            )}

            {selected && (
              <>
                <table className="grid">
                  <tbody>
                    <tr><td>Class</td><td className="mono">{selected.element.ifcClass}</td></tr>
                    <tr><td>Storey</td><td>{selected.element.storey ?? DASH}</td></tr>
                    <tr>
                      <td>GlobalId</td>
                      <td className="mono small">{selected.element.globalId}</td>
                    </tr>
                  </tbody>
                </table>

                {selected.items.length === 0 ? (
                  <p className="note-warn">
                    <b>The bill prices nothing for this element.</b> It is real work the model
                    contains and the estimate never carried — scope, not an error.
                  </p>
                ) : (
                  <>
                    <h4>In the bill</h4>

                    {/* Why this element is the colour it is.
                        The panel used to list an element's centres and leave the reader to guess
                        which one the model had painted from — so an amber slab beside a GREEN
                        concrete centre read as a contradiction rather than as the formwork centre
                        doing its job. The colour is stated, attributed, and the centres that are not
                        responsible for it are marked as such below. */}
                    {drawT !== null && reach && reach.length > 0 && (
                      <p className="muted small">
                        {driver ? (
                          <>
                            Drawn from <span className="mono">{driver.bccId}</span> (
                            <b>{driver.alertLevel}</b>) at period {clockPeriod} — the worst
                            verdict among the {reach.filter((r) => r.reached).length} of{" "}
                            {reach.length} centres that have reached this element.
                          </>
                        ) : (
                          <>
                            No centre has reached this element at period {clockPeriod}, so
                            nothing below is colouring it.
                          </>
                        )}
                      </p>
                    )}

                    {selected.items.map((item) => {
                      const centre = centres.find((c) => c.bccId === item.bccId);
                      const at = item.bccId ? reachByBcc.get(item.bccId) : undefined;
                      return (
                        <div key={item.boqItemRef} className="detail-section">
                          <div>
                            <span className="mono">{item.boqItemRef}</span>
                            <span className="muted small"> · {item.description}</span>
                            {at?.driving && (
                              // Blue, not green or amber: this marks which centre the colour came
                              // from, and must not itself read as a cost verdict.
                              <span className="pill pill-sm pill-blue" title="this element takes its colour from this centre">
                                {" "}colours it
                              </span>
                            )}
                            {at && !at.reached && (
                              <span
                                className="pill pill-sm pill-muted"
                                title={`this centre has reached ${at.position - 1} of its ${at.total} elements, and this one is number ${at.position}`}
                              >
                                {" "}not here yet
                              </span>
                            )}
                          </div>
                          <div className="muted small">
                            {item.unitRate.toLocaleString(undefined, { maximumFractionDigits: 2 })}{" "}
                            {pricing?.currency ?? "AED"}/{item.unit} · bill quantity{" "}
                            {item.boqQuantity?.toLocaleString() ?? DASH} {item.unit}
                          </div>

                          {centre ? (
                            <>
                              <div className="kpis kpis-2">
                                <div className="kpi">
                                  <div className="kpi-v">{ratio(centre.cpi)}</div>
                                  <div className="kpi-l">
                                    CPI
                                    {centre.basis !== "Measured" && (
                                      <span
                                        className={`pill pill-sm ${centre.basis === "Forecast" ? "pill-blue" : "pill-muted"}`}
                                        title={centre.basis === "Forecast"
                                          ? "Both the progress and spend projections behind this figure carry a measured error bar."
                                          : "Past at least one projection's back-tested horizon — same arithmetic, unmeasured accuracy."}
                                      >
                                        {" "}{centre.basis.toLowerCase()}
                                      </span>
                                    )}
                                  </div>
                                  <div className="kpi-sub">
                                    {centre.alertLevel}
                                    {centre.alertProjected && " (projected)"}
                                  </div>
                                </div>
                                <div className="kpi">
                                  <div className="kpi-v">
                                    {millions(centre.bac, pricing?.currency ?? "AED")}
                                  </div>
                                  <div className="kpi-l">budget at completion</div>
                                  <div className="kpi-sub">
                                    EV {millions(centre.ev, pricing?.currency ?? "AED")} · AC{" "}
                                    {centre.ac === null
                                      ? DASH
                                      : millions(centre.ac, pricing?.currency ?? "AED")}
                                  </div>
                                </div>
                              </div>

                              {/* The band, shown only where the figures are projected. A projected
                                  number without its interval invites being read as a measurement,
                                  which is precisely what it is not. */}
                              {centre.basis !== "Measured" && (
                                <div className="muted small">
                                  {centre.pctP10 != null && centre.pctP90 != null && (
                                    <>
                                      Progress {centre.pctComplete.toFixed(0)}% (
                                      {centre.pctP10.toFixed(0)}–{centre.pctP90.toFixed(0)}%)
                                    </>
                                  )}
                                  {centre.acP10 != null && centre.acP90 != null && (
                                    <>
                                      {" · "}AC {millions(centre.acP10, pricing?.currency ?? "AED")}–
                                      {millions(centre.acP90, pricing?.currency ?? "AED")}
                                    </>
                                  )}
                                  {centre.eac != null && (
                                    <>
                                      {" · "}forecast final cost{" "}
                                      <b>{millions(centre.eac, pricing?.currency ?? "AED")}</b>
                                      {/* The full amount, not millions: a variance can be a few tens
                                          of thousands, and "over by 0.0M" says nothing. */}
                                      {centre.vac != null && (
                                        <> ({centre.vac < 0 ? "over" : "under"} by{" "}
                                        {money(Math.abs(centre.vac), pricing?.currency ?? "AED")})</>
                                      )}
                                    </>
                                  )}
                                </div>
                              )}

                              {/* What stands behind the figures above.
                                  These used to be frozen at the origin and labelled as such, on the
                                  grounds that a projected percentage is no licence to invent a cost.
                                  The narrower rule is the one that holds: EV is *defined* as BAC ×
                                  percent complete, so projecting the percentage projects EV by the
                                  schema's own arithmetic. Spend is a separate forecast and stays one —
                                  where it has nothing to say, AC and everything downstream of it read
                                  as unavailable rather than being derived from progress. */}
                              {centre.basis !== "Measured" && (
                                <p className="muted small">
                                  Projected at period {centre.periodId}: EV from projected progress, AC
                                  from the spend forecast.
                                  {!centre.acAvailable && centre.acNote && <> {centre.acNote}</>}
                                  {centre.acAvailable && centre.acNote && <> {centre.acNote}</>}
                                  {" "}No PV or SPI — the baseline curve ends at period{" "}
                                  {panel?.originPeriod ?? progress?.originPeriod}.
                                  {centre.projectedFinishPeriod != null && (
                                    <>
                                      {" "}This centre reaches 100% around period{" "}
                                      <b>{centre.projectedFinishPeriod}</b> at{" "}
                                      {centre.pacePctPerPeriod.toFixed(1)} pp/period.
                                    </>
                                  )}
                                  {centre.projectedFinishPeriod == null && (
                                    <> This centre has no recent pace, so no finish period is claimed for it.</>
                                  )}
                                </p>
                              )}

                              <button
                                className="btn btn-sm btn-primary"
                                // Unclamped: the drawer now reads a projected row, and says so.
                                onClick={() => onSelectCentre?.(centre, dataPeriod)}
                              >
                                Open {centre.bccId}
                              </button>
                            </>
                          ) : (
                            <p className="muted small">
                              Cost centre <span className="mono">{item.bccId ?? DASH}</span> carries
                              no row at period {dataPeriod}.
                            </p>
                          )}
                        </div>
                      );
                    })}

                    <p className="muted small">
                      Bound at confidence {selected.element.confidence.toFixed(1)} —{" "}
                      {selected.element.confidence >= 0.9
                        ? "declared by element class."
                        : "inferred from the storey it sits on; the model carries no relationship to confirm it."}
                    </p>
                  </>
                )}
              </>
            )}
          </div>
        )}

        {pricing && (
          <div className="card">
            <h3>Priced at Tower X&apos;s rates</h3>
            <div className="kpis kpis-2">
              <div className="kpi">
                <div className="kpi-v">{millions(pricing.pricedAmount, pricing.currency)}</div>
                <div className="kpi-l">priceable scope</div>
                <div className="kpi-sub">{money(pricing.pricedAmount, pricing.currency)}</div>
              </div>
              <div className="kpi">
                <div className="kpi-v">
                  {coverage == null ? DASH : `${coverage.toFixed(0)}%`}
                </div>
                <div className="kpi-l">measurable</div>
                <div className="kpi-sub">
                  {report?.measuredElements ?? 0} of {report?.totalElements ?? 0} elements
                </div>
              </div>
            </div>

            <p className={`tie-out ${pricing.tiesOut ? "ok" : "bad"}`}>
              {pricing.tiesOut ? "✓" : "✕"} {pricing.pricedElements} priced +{" "}
              {pricing.unpricedElements} unpriced + {pricing.unmeasuredElements} unmeasured ={" "}
              {pricing.totalElements} elements in the model.
              {!pricing.tiesOut && " Elements are unaccounted for — the priced figure understates the building."}
            </p>
          </div>
        )}

        {/* One question at a time. Each label carries its own headline so switching is a choice about
            what to read next, never the only way to find out a number exists. */}
        <div className="subtabs" role="tablist" aria-label="Take-off evidence">
          {evidencePanels.map((p) => (
            <button
              key={p.key}
              type="button"
              role="tab"
              aria-selected={sidePanel === p.key}
              className={`subtab ${sidePanel === p.key ? "is-active" : ""}`}
              onClick={() => setSidePanel(p.key)}
            >
              {p.label}
              {p.hint && <span className="subtab-hint">{p.hint}</span>}
            </button>
          ))}
        </div>

        {sidePanel === "priced" && pricing && pricing.priced.length > 0 && (
          <div className="card">
            <h3>What could be priced</h3>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">Quantity</th>
                    <th className="num">Rate</th>
                    <th className="num">Amount</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.priced.map((p) => (
                    <tr key={`${p.ifcClass}-${p.measure}`}>
                      <td>
                        <span className="mono">{p.ifcClass}</span>
                        <span className="muted small"> · {p.boqItemRef}</span>
                      </td>
                      <td className="num">
                        {p.quantity.toLocaleString(undefined, { maximumFractionDigits: 1 })} {p.unit}
                      </td>
                      <td className="num">{p.unitRate.toFixed(2)}</td>
                      <td className="num">{money(p.amount, pricing.currency)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {sidePanel === "priced" && pricing && pricing.unpriced.length > 0 && (
          <div className="card">
            <h3>What could not — and why</h3>
            <p className="note-warn">
              The priced figure above is only the scope below the line. This is what it excludes.
            </p>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">Quantity</th>
                    <th className="num">Elements</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {pricing.unpriced.map((u) => (
                    <tr key={`${u.ifcClass}-${u.measure}`}>
                      <td className="mono">{u.ifcClass}</td>
                      <td className="num">
                        {u.quantity > 0
                          ? `${u.quantity.toLocaleString(undefined, { maximumFractionDigits: 1 })} ${
                              u.measure === "volume" ? "m³" : "m²"
                            }`
                          : DASH}
                      </td>
                      {/* Area rides on elements already counted under their volume line, so its
                          element count is 0 by design — never a sign of missing data. */}
                      <td className="num">{u.elementCount > 0 ? u.elementCount : DASH}</td>
                      <td className="muted small">{u.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        {sidePanel === "bill" && pricing && (pricing.quantityVariances.length > 0 || pricing.uncomparableQuantities.length > 0) && (
          <div className="card">
            <h3>Does the model agree with the bill?</h3>

            <p className="note-warn">
              <b>Read the mechanism, not the numbers below.</b> A school measured against Tower
              X&apos;s bill of quantities is two unrelated buildings, so the divergence here is not
              an overrun. On a project&apos;s <em>own</em> model this is the earliest warning in the
              whole system: every other signal waits for cost to be booked, this one fires while the
              concrete is still a drawing.
            </p>

            {pricing.quantityVariances.length > 0 && (
              <div className="grid-scroll">
                <table className="grid">
                  <thead>
                    <tr>
                      <th>BOQ item</th>
                      <th className="num">Model vs bill</th>
                      <th className="num">Variance</th>
                      <th className="num">At this rate</th>
                    </tr>
                  </thead>
                  <tbody>
                    {pricing.quantityVariances.map((v) => (
                      <tr key={v.boqItemRef}>
                        <td>
                          <div className="mono">{v.boqItemRef}</div>
                          {v.boqDescription && (
                            <div className="muted small">{v.boqDescription}</div>
                          )}
                        </td>
                        <td className="num">
                          {qty(v.modelQuantity)}
                          <span className="muted"> / </span>
                          {qty(v.boqQuantity)}
                          <div className="muted small">{v.unit}</div>
                        </td>
                        <td className="num">
                          <span className={v.variance > 0 ? "pill-warn" : ""}>
                            {v.variance > 0 ? "+" : ""}
                            {qty(v.variance)}
                          </span>
                          <div className="muted small">
                            {v.variancePct > 0 ? "+" : ""}
                            {(v.variancePct * 100).toFixed(0)}%
                          </div>
                        </td>
                        <td className="num">{money(v.costImpact, pricing.currency)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {pricing.uncomparableQuantities.length > 0 && (
              <p className="muted small">
                Not compared:{" "}
                {pricing.uncomparableQuantities.map((u) => u.boqItemRef).join(", ")} — the bill
                carries no quantity for{" "}
                {pricing.uncomparableQuantities.length === 1 ? "it" : "them"}, and treating a missing
                quantity as zero would report a 100% overrun that only exists in the gap.
              </p>
            )}

            <p className="muted small">{pricing.varianceBasis}</p>
          </div>
        )}

        {sidePanel === "measurable" && report && (
          <div className="card">
            <h3>Can this model be measured?</h3>
            {report.baseQuantitiesEmpty && (
              <p className="note-warn">
                This model carries <strong>no standard IFC BaseQuantities</strong>. A take-off written
                the textbook way returns nothing. The quantities here were read from the exporter&apos;s
                own property sets
                {report.quantityKeysSeen.length > 0 && (
                  <> — keyed on <span className="mono">{report.quantityKeysSeen.join(", ")}</span></>
                )}
                , which makes them exporter- and language-specific.
              </p>
            )}
            <table className="grid">
              <tbody>
                <tr>
                  <td>Elements</td>
                  <td className="num">{report.totalElements}</td>
                </tr>
                <tr>
                  <td>Carrying a usable quantity</td>
                  <td className="num">
                    {report.measuredElements}
                    {coverage != null && <span className="muted small"> ({coverage.toFixed(0)}%)</span>}
                  </td>
                </tr>
                <tr>
                  <td>In no building storey</td>
                  <td className="num">
                    {report.unplacedElements > 0 ? (
                      <span className="pill-warn">{report.unplacedElements}</span>
                    ) : (
                      report.unplacedElements
                    )}
                  </td>
                </tr>
                <tr>
                  <td>Storeys</td>
                  <td className="num mono">{report.storeys.length}</td>
                </tr>
              </tbody>
            </table>
            {report.storeys.length > 0 && (
              <p className="muted small">{report.storeys.join(" · ")}</p>
            )}
          </div>
        )}

        {sidePanel === "plan" && zoneMap && (
          <div className="card">
            <h3>Could this model be located in the cost plan?</h3>
            <div className="kpis kpis-2">
              <div className="kpi">
                <div className="kpi-v">{(zoneMap.matchRate * 100).toFixed(0)}%</div>
                <div className="kpi-l">elements placed</div>
                <div className="kpi-sub">
                  {zoneMap.matchedElements} of {zoneMap.totalElements} by class + storey
                </div>
              </div>
              <div className="kpi">
                <div className="kpi-v">{zoneMap.matched.length}</div>
                <div className="kpi-l">zones reached</div>
                <div className="kpi-sub">of {costMap?.zones.length ?? 0} in the cost plan</div>
              </div>
            </div>

            {index && (
              <>
                <h4>At what confidence</h4>
                <table className="grid">
                  <tbody>
                    {index.confidenceBands.map((b) => (
                      <tr key={b.confidence}>
                        <td>
                          <b>{b.label}</b>
                        </td>
                        <td className="num mono">
                          {b.confidence > 0 ? b.confidence.toFixed(1) : DASH}
                        </td>
                        <td className="num">
                          {b.elementCount}
                          <span className="muted small">
                            {" "}({pct(b.elementCount, index.map.totalElements)})
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>

                <p className="muted small">{index.map.mappingBasis}</p>

                {/* Reference, not a finding: the rule-by-rule audit trail, wanted occasionally and
                    long enough to bury everything under it. Collapsed, with its row count on the
                    summary so its size is never a surprise. */}
                <details className="drill">
                  <summary>
                    The bindings
                    <span className="muted small"> · {index.map.rules.length} rules</span>
                  </summary>
                  <div className="grid-scroll">
                    <table className="grid">
                      <thead>
                        <tr>
                          <th>Class</th>
                          <th>BOQ item</th>
                          <th className="num">n</th>
                          <th>Why</th>
                        </tr>
                      </thead>
                      <tbody>
                        {index.map.rules.map((r) => (
                          <tr key={`${r.ifcClass}-${r.boqItemRef}`}>
                            <td className="mono small">{r.ifcClass}</td>
                            <td className="mono">
                              {r.boqItemRef}
                              <span className="muted small"> {r.role}</span>
                            </td>
                            <td className="num">{r.elementCount}</td>
                            <td className="muted small">{r.basis}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </details>

                {index.map.unmapped.length > 0 && (
                  <>
                    <h4>In the model, not in the bill</h4>
                    <p className="note-warn">
                      These are not failures to map. They are scope the estimate never priced —
                      the earliest kind of gap a QS can act on.
                    </p>
                    <table className="grid">
                      <tbody>
                        {index.map.unmapped.map((u) => (
                          <tr key={u.ifcClass}>
                            <td className="mono">{u.ifcClass}</td>
                            <td className="num">{u.elementCount}</td>
                            <td className="muted small">{u.reason}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </>
                )}

                {index.notInModel > 0 && (
                  <p className="note-warn">
                    {index.notInModel} element{index.notInModel === 1 ? "" : "s"} in the register
                    could not be found in the loaded file — the register and this model have drifted
                    apart.
                  </p>
                )}
              </>
            )}

            {!index && links && (
              <>
                <h4>At what confidence</h4>
                <table className="grid">
                  <tbody>
                    <tr>
                      <td>
                        <b>Direct</b>
                        <span className="muted small">
                          {" "}· the element&apos;s own properties name a zone
                        </span>
                      </td>
                      <td className="num mono">{TIER_CONFIDENCE.Direct.toFixed(2)}</td>
                      <td className="num">
                        {links.directCount}
                        <span className="muted small">
                          {" "}({pct(links.directCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                    <tr>
                      <td>
                        <b>Grouped</b>
                        <span className="muted small"> · placed by a class + storey rule</span>
                      </td>
                      <td className="num mono">{TIER_CONFIDENCE.Grouped.toFixed(2)}</td>
                      <td className="num">
                        {links.groupedCount}
                        <span className="muted small">
                          {" "}({pct(links.groupedCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                    <tr>
                      <td>
                        <b>None</b>
                        <span className="muted small"> · no rule reached it</span>
                      </td>
                      <td className="num muted">{DASH}</td>
                      <td className="num">
                        {links.noneCount}
                        <span className="muted small">
                          {" "}({pct(links.noneCount, links.totalElements)})
                        </span>
                      </td>
                    </tr>
                  </tbody>
                </table>

                {links.directCount === 0 && (
                  <p className="note-warn">
                    <b>Nothing in this model links directly.</b> Not one of its{" "}
                    {links.totalElements} elements carries a cost code in its property sets, so every
                    placement above is a rule&apos;s inference about a category rather than a
                    statement by whoever authored the model. That is normal for a structural export
                    — and it is exactly the ceiling a QS should know about before trusting a
                    model-driven cost figure. Elements are drawn at reduced opacity to say so.
                  </p>
                )}

                {links.codeCarryingElements > 0 && (
                  <p className="muted small">
                    {links.codeCarryingElements} element
                    {links.codeCarryingElements === 1 ? "" : "s"} carry a recognised cost identifier
                    {links.codesFound.length > 0 && (
                      <> — <span className="mono">{links.codesFound.slice(0, 8).join(", ")}</span></>
                    )}
                    .
                  </p>
                )}
              </>
            )}

            <p className="note-warn">
              This shows the <b>mechanism</b>, not a budget. The loaded model is a school and the zones
              belong to Tower X — a matched element means &ldquo;an element of this kind would map
              here&rdquo;, never that it shares that budget.
            </p>

            <details className="drill">
              <summary>
                Which zones it reached
                <span className="muted small"> · {zoneMap.matched.length} zones</span>
              </summary>
              <div className="grid-scroll">
                <table className="grid">
                  <thead>
                    <tr>
                      <th>Zone</th>
                      <th className="num">Elements</th>
                      <th>From</th>
                    </tr>
                  </thead>
                  <tbody>
                    {zoneMap.matched.map((m) => (
                      <tr key={m.zoneCode}>
                        <td className="mono">{m.zoneCode}</td>
                        <td className="num">{m.elementCount}</td>
                        <td className="muted small mono">{m.ifcClasses.join(", ")}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </details>

            {zoneMap.unmatched.length > 0 && (
              <p className="muted small">
                No rule placed{" "}
                {zoneMap.unmatched.map((u) => `${u.ifcClass} (${u.elementCount})`).join(", ")}.
              </p>
            )}

            {zoneMap.zonesWithNoGeometry.length > 0 && (
              <p className="muted small">
                <b>{zoneMap.zonesWithNoGeometry.length} of Tower X&apos;s zones got nothing from this
                model</b> — {zoneMap.zonesWithNoGeometry.join(", ")}. A structural model carries no MEP,
                finishes or landscaping, and a match rate that ignored that would flatter itself.
              </p>
            )}
          </div>
        )}

        {sidePanel === "measurable" && measurement && (
          <div className="card">
            <h3>Measured by class</h3>
            <div className="grid-scroll">
              <table className="grid">
                <thead>
                  <tr>
                    <th>Class</th>
                    <th className="num">n</th>
                    <th className="num">Volume m³</th>
                    <th className="num">Area m²</th>
                  </tr>
                </thead>
                <tbody>
                  {measurement.byClass.map((c) => (
                    <tr key={c.ifcClass}>
                      <td className="mono">{c.ifcClass}</td>
                      <td className="num">{c.elementCount}</td>
                      <td className="num">
                        {c.volume > 0
                          ? c.volume.toLocaleString(undefined, { maximumFractionDigits: 1 })
                          : DASH}
                      </td>
                      <td className="num">
                        {c.area > 0
                          ? c.area.toLocaleString(undefined, { maximumFractionDigits: 1 })
                          : DASH}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
