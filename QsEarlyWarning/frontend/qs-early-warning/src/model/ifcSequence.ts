import type { CostCentreEvm } from "../api/client";
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

/** Per-cost-centre progress and alert at one point in time. */
export interface CentreState {
  /** 0..1 — actual percent complete, interpolated between reporting periods. */
  progress: number;
  alertLevel: string;
}

/**
 * The state of every centre at a fractional period.
 *
 * Periods are monthly, so stepping straight from one to the next makes the building jump. Progress
 * is interpolated between the two surrounding periods to give a continuous rise; the alert level is
 * taken from the nearer period rather than blended, because an alert is a verdict and there is no
 * such thing as being 40% AMBER.
 */
export function statesAt(
  t: number,
  centresByPeriod: Map<number, CostCentreEvm[]>,
): Map<string, CentreState> {
  const lo = Math.floor(t);
  const hi = Math.ceil(t);
  const frac = hi === lo ? 0 : t - lo;

  const loRows = centresByPeriod.get(lo) ?? [];
  const hiRows = centresByPeriod.get(hi) ?? loRows;
  const hiByBcc = new Map(hiRows.map((c) => [c.bccId, c]));
  const nearer = frac < 0.5 ? lo : hi;
  const nearerByBcc = new Map((centresByPeriod.get(nearer) ?? loRows).map((c) => [c.bccId, c]));

  const out = new Map<string, CentreState>();
  for (const low of loRows) {
    const high = hiByBcc.get(low.bccId);
    const a = (low.actualPct ?? 0) / 100;
    const b = ((high?.actualPct ?? low.actualPct) ?? 0) / 100;
    out.set(low.bccId, {
      progress: Math.max(0, Math.min(1, a + (b - a) * frac)),
      alertLevel: nearerByBcc.get(low.bccId)?.alertLevel ?? low.alertLevel,
    });
  }
  return out;
}

export interface SequenceFrame {
  /** localIds standing at this moment. */
  built: Set<number>;
  /** localId → the alert of the worst centre that has built it, for colouring. */
  alertByLocalId: Map<number, string>;
  /** How many mapped elements are standing, for the on-screen readout. */
  builtCount: number;
}

/**
 * Which elements stand at a fractional period, and what each of them reads as.
 *
 * An element is shown as soon as ANY of its centres has reached it. A slab whose concrete is poured
 * but whose formwork trade is behind is still a slab that exists — waiting for every trade would
 * make the building lag its own concrete.
 */
export function frameAt(
  t: number,
  sequence: BuildSequence,
  centresByPeriod: Map<number, CostCentreEvm[]>,
): SequenceFrame {
  const states = statesAt(t, centresByPeriod);
  const built = new Set<number>();
  const alertByLocalId = new Map<number, string>();

  for (const [bccId, ordered] of sequence) {
    const state = states.get(bccId);
    if (!state || state.progress <= 0) continue;

    const count = Math.floor(state.progress * ordered.length);
    for (let i = 0; i < count; i++) {
      const localId = ordered[i];
      built.add(localId);
      // Worst wins: an element standing on two trades reads as the one in trouble.
      const seen = alertByLocalId.get(localId);
      if (seen !== "AMBER") alertByLocalId.set(localId, state.alertLevel);
    }
  }

  return { built, alertByLocalId, builtCount: built.size };
}
