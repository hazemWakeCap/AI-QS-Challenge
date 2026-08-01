import type {
  CostCentreEvm, ProgressForecast, ProgressHorizonMetric, ProgressPoint, ProjectedPanel,
} from "../api/client";
import type { ElementMapIndex } from "./ifcElementMap";

/**
 * A construction sequence for the model, driven by the sheet's progress curves.
 *
 * <b>The one thing that is not in the data, stated plainly.</b> `9_HISTORICAL_DATA` carries
 * `Actual_Pct_Complete` per <i>cost centre</i> per period — never per element. A centre reading 43%
 * says nothing about <i>which</i> 43% of its 299 slabs are poured. So the order elements appear in
 * is an assumption this module makes, not a fact the workbook contains.
 *
 * <b>The assumption: buildings go up from the bottom.</b> Within a cost centre, elements are ordered
 * by the storey they sit on and then by GlobalId, and the first <i>n</i> of them are shown once the
 * centre's actual percent complete reaches <i>n / total</i>. That is what every 4D planning tool
 * does, and it is defensible for concrete frame work — but it is a sequence we chose, and the UI
 * labels it as one.
 *
 * <b>What IS from the data:</b> how much of each centre is complete at each period, and therefore
 * how much of the model stands; and the alert level that colours it. The shape of the curve, the
 * pace, and the drift are all the workbook's.
 *
 * <b>Past the last reported period</b> the same machinery runs on a projection instead of a
 * measurement — see {@link ProgressIndex}. Nothing about the ordering changes; what changes is where
 * the percentage came from, and every frame carries the tier that says which it was. A projected
 * element is never drawn like a built one: the band between the pessimistic and optimistic
 * projections is rendered as translucent geometry, so the uncertainty is in the picture rather than
 * in a caption beside it.
 *
 * <b>Colour past the origin comes from the projected EVM panel</b> — see {@link ProjectedVerdicts}.
 * The alert a period shows and the CPI printed beside it are then one number read twice, not two
 * verdicts that can disagree.
 */

/** Bottom-up storey ranking. Anything unrecognised sorts last, so it never displaces real levels. */
const STOREY_RANK: Record<string, number> = {
  "SUB LEVEL": 0,
  "01 - ENTRY LEVEL": 1,
  "02 - FLOOR": 2,
  "03 - FLOOR": 3,
  ROOF: 4,
};

function rankOf(storey: string | null): number {
  if (!storey) return 99;
  return STOREY_RANK[storey.trim().toUpperCase()] ?? 98;
}

/** Ordered localIds per cost centre — the order the sequence reveals them in. */
export type BuildSequence = Map<string, number[]>;

/**
 * Orders every mapped element within its cost centre, bottom storey first.
 *
 * An element belonging to several centres (a slab is concrete AND formwork) appears in each of
 * them: the two trades progress independently in the sheet, and tying a slab's appearance to only
 * one of them would silently pick a winner.
 */
export function buildSequence(index: ElementMapIndex): BuildSequence {
  const byCentre = new Map<string, { localId: number; rank: number; globalId: string }[]>();

  for (const localId of index.mappedLocalIds) {
    const resolved = index.byLocalId.get(localId);
    if (!resolved) continue;
    const rank = rankOf(resolved.element.storey);

    for (const item of resolved.items) {
      if (!item.bccId) continue;
      const list = byCentre.get(item.bccId) ?? [];
      list.push({ localId, rank, globalId: resolved.element.globalId });
      byCentre.set(item.bccId, list);
    }
  }

  const out: BuildSequence = new Map();
  for (const [bccId, list] of byCentre) {
    // GlobalId breaks ties so the sequence is identical on every run — a video rendered twice must
    // not differ.
    list.sort((a, b) => a.rank - b.rank || (a.globalId < b.globalId ? -1 : 1));
    out.set(bccId, list.map((e) => e.localId));
  }
  return out;
}

/** Where a period's progress figure came from, and therefore how firmly it may be drawn. */
export type SequenceTier = "measured" | "forecast" | "extrapolated";

/**
 * The projection, indexed for lookup by centre and period.
 *
 * Built once per model from one API response. Holding it as a lookup rather than re-deriving it per
 * frame matters: playback draws a frame every 120 ms and each one asks about every centre.
 */
export interface ProgressIndex {
  /** Last period the workbook actually reports. At or below this, nothing is projected. */
  originPeriod: number;
  /** Last period the projection covers. */
  horizonPeriod: number;
  /** Last period with a measured error bar behind it. Past this the projection is unvalidated. */
  backtestedThroughPeriod: number;
  byCentre: Map<string, Map<number, ProgressPoint>>;
  /** Carried-forward alert per centre — no verdict exists for a period that has not happened. */
  alertByCentre: Map<string, string | null>;
  /** Projected finish per centre; absent when the centre has no pace to project. */
  finishByCentre: Map<string, number | null>;
  /** Pace per centre, in percentage points per period — what the projection is riding on. */
  paceByCentre: Map<string, number>;
  /** The back-test the UI quotes as its warrant. Carried here so the caption cannot drift from it. */
  accuracy: ProgressHorizonMetric[];
  method: string;
}

export function buildProgressIndex(f: ProgressForecast): ProgressIndex {
  const byCentre = new Map<string, Map<number, ProgressPoint>>();
  const alertByCentre = new Map<string, string | null>();
  const finishByCentre = new Map<string, number | null>();
  const paceByCentre = new Map<string, number>();

  for (const c of f.centres) {
    byCentre.set(c.bccId, new Map(c.points.map((p) => [p.period, p])));
    alertByCentre.set(c.bccId, c.alertAtOrigin);
    finishByCentre.set(c.bccId, c.projectedFinishPeriod);
    paceByCentre.set(c.bccId, c.pacePctPerPeriod);
  }

  return {
    originPeriod: f.originPeriod,
    horizonPeriod: f.horizonPeriod,
    backtestedThroughPeriod: f.backtestedThroughPeriod,
    byCentre,
    alertByCentre,
    finishByCentre,
    paceByCentre,
    accuracy: f.validation.metrics,
    method: f.method,
  };
}

/**
 * Per-period cost verdicts past the origin — `period → bccId → alertLevel`.
 *
 * <b>Why this exists.</b> The progress forecaster carries one alert per centre, the one it read at
 * the origin, because progress is all it forecasts: a projected percentage on its own says nothing
 * about cost. So every projected period was painted with the origin's verdict, frozen.
 *
 * That was defensible only while nothing else on the tab had an opinion. `EvmProjector` now states a
 * cost position for each projected period — EV from the projected percentage, AC from the spend
 * cone, CPI from the two — and the panel beside the model prints it. Leaving the paint on the origin
 * alert put two verdicts about one centre on one screen: BCC-STR-CON-205 was AMBER at period 12 on
 * CPI 0.933, recovers to 0.969 by period 13, and the model went on colouring it as drifting while
 * the KPI panel a few pixels away read GREEN. The picture was wrong and the reader had no way to
 * tell which half to believe.
 *
 * The panel is the one that knows, so the panel is what colours the model.
 */
export type ProjectedVerdicts = Map<number, Map<string, string>>;

/**
 * Indexes the prefetched panels by period and centre.
 *
 * Measured periods are included and harmless: `statesAt` reads the reported rows below the origin
 * and never consults this, and the panel is a passthrough of those same rows anyway.
 */
export function buildProjectedVerdicts(
  panelByPeriod: ReadonlyMap<number, ProjectedPanel>,
): ProjectedVerdicts {
  const out: ProjectedVerdicts = new Map();
  for (const [period, panel] of panelByPeriod) {
    out.set(period, new Map(panel.centres.map((c) => [c.bccId, c.alertLevel])));
  }
  return out;
}

/** The tier a period falls in, given the projection's own boundaries. */
export function tierAt(period: number, progress: ProgressIndex | null): SequenceTier {
  if (!progress || period <= progress.originPeriod) return "measured";
  return period <= progress.backtestedThroughPeriod ? "forecast" : "extrapolated";
}

/** Per-cost-centre progress and alert at one point in time. */
export interface CentreState {
  /** 0..1 — the median position: actual percent complete measured, or projected past the origin. */
  progress: number;
  /**
   * 0..1 — the pessimistic and optimistic ends of the projection.
   *
   * Both equal `progress` for measured periods, because a measurement has no band. Past the origin
   * they widen, and the elements between them are what gets drawn as the translucent shell.
   */
  progressLow: number;
  progressHigh: number;
  alertLevel: string;
  tier: SequenceTier;
}

/**
 * The state of every centre at a fractional period.
 *
 * Periods are monthly, so stepping straight from one to the next makes the building jump. Progress
 * is interpolated between the two surrounding periods to give a continuous rise; the alert level is
 * taken from the nearer period rather than blended, because an alert is a verdict and there is no
 * such thing as being 40% AMBER.
 *
 * Past `progress.originPeriod` the percentages come from the projection instead of the workbook, by
 * exactly the same interpolation, and the alert comes from that period's projected EVM panel — the
 * same verdict, off the same projected CPI, that the figures beside the model are printed from. It
 * still carries no colour of its own: a projected AMBER centre is drawn amber, and what marks the
 * frame as a projection is opacity, not hue. Without a panel for the period — before the prefetch
 * lands, or for a centre the projector could not price — it falls back to the origin's verdict,
 * which is the same fallback `EvmProjector` itself applies when AC is unavailable.
 */
export function statesAt(
  t: number,
  centresByPeriod: Map<number, CostCentreEvm[]>,
  progress: ProgressIndex | null = null,
  verdicts: ProjectedVerdicts | null = null,
): Map<string, CentreState> {
  const lo = Math.floor(t);
  const hi = Math.ceil(t);
  const frac = hi === lo ? 0 : t - lo;

  // Past the origin the workbook has no rows at all, so the roster of centres to walk comes from the
  // projection. At or below it, the measured rows are the roster exactly as before.
  const projecting = progress !== null && hi > progress.originPeriod;

  const loRows = centresByPeriod.get(lo) ?? [];
  const hiRows = centresByPeriod.get(hi) ?? loRows;
  const hiByBcc = new Map(hiRows.map((c) => [c.bccId, c]));
  const nearer = frac < 0.5 ? lo : hi;
  const nearerByBcc = new Map((centresByPeriod.get(nearer) ?? loRows).map((c) => [c.bccId, c]));

  const out = new Map<string, CentreState>();
  const tier = tierAt(Math.round(t), progress);

  if (!projecting) {
    for (const low of loRows) {
      const high = hiByBcc.get(low.bccId);
      const a = (low.actualPct ?? 0) / 100;
      const b = ((high?.actualPct ?? low.actualPct) ?? 0) / 100;
      const p = clamp01(a + (b - a) * frac);
      out.set(low.bccId, {
        progress: p,
        progressLow: p,
        progressHigh: p,
        alertLevel: nearerByBcc.get(low.bccId)?.alertLevel ?? low.alertLevel,
        tier: "measured",
      });
    }
    return out;
  }

  for (const [bccId, points] of progress!.byCentre) {
    const a = points.get(lo);
    const b = points.get(hi) ?? a;
    if (!a || !b) continue;

    // The measured rows still own the alert while `lo` is a reported period, so a frame straddling
    // the boundary does not flip colour a beat early. Past it the projected panel owns it, and the
    // origin's verdict is only what is left when neither has anything to say.
    const measuredAlert = nearerByBcc.get(bccId)?.alertLevel;
    const alertLevel = (nearer <= progress!.originPeriod
        ? measuredAlert
        : verdicts?.get(nearer)?.get(bccId))
      ?? progress!.alertByCentre.get(bccId)
      ?? measuredAlert
      ?? "";

    out.set(bccId, {
      progress: lerp01(a.p50Pct, b.p50Pct, frac),
      progressLow: lerp01(a.p10Pct ?? a.p50Pct, b.p10Pct ?? b.p50Pct, frac),
      progressHigh: lerp01(a.p90Pct ?? a.p50Pct, b.p90Pct ?? b.p50Pct, frac),
      alertLevel,
      tier,
    });
  }
  return out;
}

const clamp01 = (v: number) => Math.max(0, Math.min(1, v));
const lerp01 = (a: number, b: number, frac: number) => clamp01((a + (b - a) * frac) / 100);

export interface SequenceFrame {
  /**
   * localIds standing at this moment — the median projection, and the whole truth for measured
   * periods. Unchanged in meaning from before the projection existed.
   */
  built: Set<number>;
  /**
   * Subset of `built` that stands even on the pessimistic projection. Drawn solid.
   *
   * Identical to `built` for measured periods: a reported percentage is not a range.
   */
  confident: Set<number>;
  /**
   * Elements between the median and optimistic projections — work that may or may not be there by
   * this period. Drawn translucent, so the interval is visible as geometry rather than as a caption.
   */
  shell: Set<number>;
  /** localId → the alert of the worst centre that has built it, for colouring. */
  alertByLocalId: Map<number, string>;
  /** How many mapped elements are standing, for the on-screen readout. */
  builtCount: number;
  /** How many more might be, at the optimistic end. */
  shellCount: number;
  /** What stands behind this frame's percentages. */
  tier: SequenceTier;
}

/**
 * Which elements stand at a fractional period, and what each of them reads as.
 *
 * An element is shown as soon as ANY of its centres has reached it. A slab whose concrete is poured
 * but whose formwork trade is behind is still a slab that exists — waiting for every trade would
 * make the building lag its own concrete.
 *
 * The same "any centre" rule governs the shell, and it resolves the same way: an element inside one
 * trade's band but firmly built by another is built, not uncertain. Certainty wins over doubt in the
 * same direction that progress wins over absence.
 */
export function frameAt(
  t: number,
  sequence: BuildSequence,
  centresByPeriod: Map<number, CostCentreEvm[]>,
  progress: ProgressIndex | null = null,
  verdicts: ProjectedVerdicts | null = null,
): SequenceFrame {
  const states = statesAt(t, centresByPeriod, progress, verdicts);
  const built = new Set<number>();
  const confident = new Set<number>();
  const shell = new Set<number>();
  const alertByLocalId = new Map<number, string>();
  let tier: SequenceTier = "measured";

  for (const [bccId, ordered] of sequence) {
    const state = states.get(bccId);
    if (!state) continue;
    if (state.tier !== "measured") tier = state.tier;
    if (state.progressHigh <= 0) continue;

    const nConfident = Math.floor(state.progressLow * ordered.length);
    const nBuilt = Math.floor(state.progress * ordered.length);
    const nShell = Math.floor(state.progressHigh * ordered.length);

    for (let i = 0; i < nShell; i++) {
      const localId = ordered[i];
      if (i < nConfident) confident.add(localId);
      if (i < nBuilt) built.add(localId);
      else shell.add(localId);

      // Worst wins: an element standing on two trades reads as the one in trouble.
      const seen = alertByLocalId.get(localId);
      if (seen !== "AMBER") alertByLocalId.set(localId, state.alertLevel);
    }
  }

  // An element another trade has firmly built is not uncertain, whatever this trade's band says.
  for (const localId of built) shell.delete(localId);

  return { built, confident, shell, alertByLocalId, builtCount: built.size, shellCount: shell.size, tier };
}

/** Where one cost centre stands in relation to one element, at one moment. */
export interface CentreReach {
  bccId: string;
  /**
   * Whether this centre's progress has actually reached this element yet.
   *
   * The distinction the UI was missing. A slab is concrete AND soffit formwork, and at period 8 the
   * concrete centre has poured 151 of 299 slabs while the formwork centre has struck 106 — so slabs
   * 107–151 are bound to an AMBER formwork centre that has not got to them. They are painted by the
   * concrete verdict alone, and correctly read green.
   */
  reached: boolean;
  /** This centre's verdict at this period, whether or not it has reached the element. */
  alertLevel: string;
  /** Whether this is the centre the element's colour was taken from. At most one is. */
  driving: boolean;
  /** 1-based position of the element in this centre's build order, and how many it carries. */
  position: number;
  total: number;
}

/**
 * Why one element reads the colour it does.
 *
 * <b>Not a second opinion — the same arithmetic, asked about one element.</b> `frameAt` decides an
 * element's colour by walking every centre that has reached it and letting the worst win, but it
 * returns only the verdict, so the panel beside the model could list an element's centres and never
 * say which of them was the one on screen. That is what made a slab painted amber sit next to a
 * cost centre reading GREEN with nothing to reconcile them: the centre driving the colour was the
 * *other* one in the list, and a third of the slabs standing at that period were not bound to it yet
 * at all.
 *
 * Called once per selection, never per frame, so walking the centres to find the element is cheap
 * where doing it for 1,127 elements thirty times a second would not be. The test suite pins its
 * answer against `frameAt`'s, so the two cannot drift into disagreeing about one element.
 */
export function reachOf(
  localId: number,
  t: number,
  sequence: BuildSequence,
  centresByPeriod: Map<number, CostCentreEvm[]>,
  progress: ProgressIndex | null = null,
  verdicts: ProjectedVerdicts | null = null,
): CentreReach[] {
  const states = statesAt(t, centresByPeriod, progress, verdicts);
  const out: CentreReach[] = [];

  for (const [bccId, ordered] of sequence) {
    const position = ordered.indexOf(localId);
    if (position < 0) continue;
    const state = states.get(bccId);
    if (!state) continue;

    // `frameAt` colours everything up to the optimistic end, so that is the line reach is read at.
    // On a measured period the band is a point and this is simply "has this centre got here".
    const nReached = Math.floor(state.progressHigh * ordered.length);
    out.push({
      bccId,
      reached: position < nReached,
      alertLevel: state.alertLevel,
      driving: false,
      position: position + 1,
      total: ordered.length,
    });
  }

  // The painter's own rule, restated over the same list in the same order: the first AMBER to reach
  // an element locks it, and failing that the last centre to reach it wins. Deriving the driver here
  // rather than in the component is what stops a second opinion about what AMBER outranks.
  let driver = -1;
  for (let i = 0; i < out.length; i++) {
    if (!out[i].reached) continue;
    if (driver >= 0 && out[driver].alertLevel === "AMBER") break;
    driver = i;
  }
  if (driver >= 0) out[driver].driving = true;

  return out;
}
