import type { CostCentreEvm } from "../api/client";

/**
 * The numbers the build-sequence overlay reads out: what is rising at this period, and where the
 * money and the programme stand at the same moment.
 *
 * <b>Why this is a module and not arithmetic inside the overlay.</b> The stage is drawn twice — as
 * DOM for the headless renderer and again into a 2D canvas for the in-browser recorder — so anything
 * computed at the drawing site is computed twice and can disagree with itself. Worse, these
 * particular figures also appear on the EVM tab, and a video quoting a different CPI from the
 * dashboard it was exported from is a video nobody can defend in a meeting.
 *
 * So the totals here are the same sum the API performs, over the same rows the API returns; see
 * {@link totalsOf}. Nothing is re-derived from a different grain.
 */

/** Project-wide EVM at one period, summed from the cost-centre rows. */
export interface PeriodTotals {
  period: number;
  bac: number;
  pv: number;
  ev: number;
  ac: number;
  /** EV − AC. Negative means more has been spent than earned. */
  cv: number;
  cpi: number | null;
  spi: number | null;
  eac: number;
  /** BAC − EAC. Negative means forecast to overrun. */
  vac: number;
  /** EV ÷ BAC as a percentage — how much of the bill has actually been earned. */
  earnedPct: number | null;
  /** PV ÷ BAC as a percentage — how much of it should have been by now. */
  plannedPct: number | null;
}

/**
 * Sums one period's cost-centre rows into project totals.
 *
 * <b>Deliberately a mirror, not an alternative.</b> This is line-for-line the backend's
 * `DashboardController.Totals` — same sums, same CPI-method EAC, same fall back to BAC when nothing
 * has been earned yet. The overlay could have called `/api/v1/evm` instead, but the stage already
 * holds every period's rows in memory to drive the sequence, and a second round trip per frame to
 * fetch a figure it can add up is a worse trade than a mirrored formula with a test pinning it.
 */
export function totalsOf(period: number, rows: readonly CostCentreEvm[]): PeriodTotals {
  let bac = 0;
  let pv = 0;
  let ev = 0;
  let ac = 0;
  for (const r of rows) {
    bac += r.bac;
    pv += r.pv;
    ev += r.ev;
    ac += r.ac;
  }

  // CPI-method EAC. Nothing earned means no productivity to extrapolate from, so the forecast is
  // the budget rather than a division by zero.
  const eac = ev !== 0 ? (bac * ac) / ev : bac;

  return {
    period,
    bac, pv, ev, ac,
    cv: ev - ac,
    cpi: ac !== 0 ? ev / ac : null,
    spi: pv !== 0 ? ev / pv : null,
    eac,
    vac: bac - eac,
    earnedPct: bac !== 0 ? (100 * ev) / bac : null,
    plannedPct: bac !== 0 ? (100 * pv) / bac : null,
  };
}

/** One cost centre that gained ground in the period being drawn. */
export interface RisingCentre {
  bccId: string;
  discipline: string | null;
  packageCode: string;
  alertLevel: string;
  /** Percent complete at this period, as the workbook reports it. */
  actualPct: number;
  /** Percentage points gained since the period before. Always positive — see {@link frameReadout}. */
  deltaPp: number;
  bac: number;
  /** BAC × points gained: roughly what this period's work on this centre was worth. */
  earned: number;
}

/** Everything the overlay states about one frame beyond the element counts. */
export interface FrameReadout {
  period: number;
  /**
   * The centres <i>on screen</i> that moved this period, most valuable work first.
   *
   * Scoped to the model on purpose. The project has 173 cost centres and the register reaches only
   * the structure; naming a rising fit-out package beside a picture in which nothing fit-out can
   * possibly move would be the overlay describing something the viewer cannot see.
   */
  rising: RisingCentre[];
  /** On-model centres that moved but did not make the list. */
  risingMore: number;
  /** Centres moving anywhere on the project, on model or not — the wider scope, stated as such. */
  projectMoving: number;
  /** Distinct disciplines the model's centres belong to. Stable across frames, so the heading is. */
  disciplines: string[];
  /** What the centres on screen are worth, against {@link PeriodTotals.bac}. */
  onModelBac: number;
  /** Project-wide, every centre — never just the ones in the picture. */
  totals: PeriodTotals;
}

/**
 * What the overlay says at one period.
 *
 * `onModel` is the set of cost centres the build sequence can actually show — its own keys. Passing
 * it in rather than deriving it here keeps this module free of the element register.
 *
 * Movement is measured against the previous period's reported percentage. At the first period there
 * is no previous row, so a centre already showing progress reads as having gained all of it, which
 * is the honest reading of a series that starts there.
 */
export function frameReadout(
  period: number,
  centresByPeriod: ReadonlyMap<number, CostCentreEvm[]>,
  onModel: ReadonlySet<string>,
  limit = 3,
): FrameReadout | null {
  const rows = centresByPeriod.get(period);
  if (!rows) return null;

  const before = new Map(
    (centresByPeriod.get(period - 1) ?? []).map((r) => [r.bccId, r.actualPct ?? 0]),
  );

  const rising: RisingCentre[] = [];
  const disciplines: string[] = [];
  const seenDiscipline = new Set<string>();
  let projectMoving = 0;
  let onModelBac = 0;

  for (const r of rows) {
    const actualPct = r.actualPct ?? 0;
    const deltaPp = actualPct - (before.get(r.bccId) ?? 0);
    if (deltaPp > 0) projectMoving++;

    if (!onModel.has(r.bccId)) continue;
    onModelBac += r.bac;
    if (r.discipline && !seenDiscipline.has(r.discipline)) {
      seenDiscipline.add(r.discipline);
      disciplines.push(r.discipline);
    }
    if (deltaPp <= 0) continue;

    rising.push({
      bccId: r.bccId,
      discipline: r.discipline,
      packageCode: r.packageCode,
      alertLevel: r.alertLevel,
      actualPct,
      deltaPp,
      bac: r.bac,
      earned: (r.bac * deltaPp) / 100,
    });
  }

  // Richest first, then by id — a video rendered twice must not shuffle its own caption.
  rising.sort((a, b) => b.earned - a.earned || (a.bccId < b.bccId ? -1 : 1));

  return {
    period,
    rising: rising.slice(0, limit),
    risingMore: Math.max(0, rising.length - limit),
    projectMoving,
    disciplines,
    onModelBac,
    totals: totalsOf(period, rows),
  };
}
