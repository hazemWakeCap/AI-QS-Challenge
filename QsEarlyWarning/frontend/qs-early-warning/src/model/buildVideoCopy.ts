import { millions, millionsBare, pct, ratio } from "../format";
import type { FrameReadout } from "./buildVideoStats";

/**
 * Every word that appears on the build-sequence stage, in one place.
 *
 * <b>Why this is a module and not just JSX.</b> The stage is drawn twice by two different
 * mechanisms: as DOM, which the headless renderer captures with the rest of the page, and again into
 * a 2D canvas, because `captureStream()` sees only the WebGL canvas and would silently drop every
 * bit of text. Two copies of the wording is two copies that can drift, and the one that would drift
 * unnoticed is the caption — the line that stops the video being read as a claim the data does not
 * support. So both draw from here.
 */

export const VIDEO_TITLE = "Tower X · build sequence";

export const VIDEO_SUBTITLE =
  "Structure rises at each cost centre's recorded progress · coloured by that centre's alert level";

/**
 * The load-bearing sentence.
 *
 * The sheet records percent complete per cost centre and never per element, so the order elements
 * appear in is ours, not the workbook's. A construction sequence that looks authoritative is exactly
 * the kind of thing people stop questioning, which is why this rides on every frame rather than
 * living in a doc nobody opens.
 */
export const VIDEO_CAPTION_LEAD = "The order is assumed, the amounts are not.";
export const VIDEO_CAPTION_BODY =
  "The sheet records percent complete per cost centre, never per element, so elements rise "
  + "bottom-up within their trade while the pace and the colour come from the workbook.";

/** Wording for the two counts in the top-right readout. */
export const standingLabel = (built: number, mapped: number) =>
  `${built.toLocaleString()} of ${mapped.toLocaleString()} priced elements standing`;

export const unpricedLabel = (unpriced: number) =>
  `${unpriced.toLocaleString()} elements the bill never priced — they never build`;

export const periodLabel = (period: number) => `Period ${period}`;

/** The legend entry for elements no bill item ever paid for. */
export const NOT_IN_BILL_LABEL = "Not in the bill";

/**
 * The calendar month a period stands for, parsed rather than localised.
 *
 * `toLocaleDateString` would give a different string on a differently-configured machine, and
 * `new Date(...).getUTCMonth()` shifts Oct-2025 back into September for anyone east of Greenwich,
 * because the API serialises a bare local midnight. The video's frames must be reproducible, so the
 * date is read out of the string it arrived in.
 */
const MONTHS = [
  "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
];

export function monthLabel(periodStart: string | null | undefined): string | null {
  const m = /^(\d{4})-(\d{2})/.exec(periodStart ?? "");
  if (!m) return null;
  const month = MONTHS[Number(m[2]) - 1];
  return month ? `${month} ${m[1]}` : null;
}

/* ── The two readouts added beside the model ──
   One says what is going up right now and what trade it belongs to; the other says what the money
   and the programme read at the same period. Both are drawn by the DOM overlay and the canvas
   compositor, so like the caption they are formatted here exactly once. */

export const RISING_LABEL = "Rising now";
export const PROJECT_LABEL = "Project to date";

/** "Rising now · Structural Works" — the disciplines the model can actually show moving. */
export function risingHeading(disciplines: readonly string[]): string {
  if (disciplines.length === 0) return RISING_LABEL;
  const shown = disciplines.slice(0, 2).join(" · ");
  const rest = disciplines.length - 2;
  return rest > 0 ? `${RISING_LABEL} · ${shown} +${rest} more` : `${RISING_LABEL} · ${shown}`;
}

export const risingPctLabel = (actualPct: number) => `${Math.round(actualPct)}%`;
export const risingDeltaLabel = (deltaPp: number) => `+${deltaPp.toFixed(1)}pp`;

/**
 * The line under the rising list, which exists to stop the list reading as the whole project.
 *
 * The model reaches the structure centres and nothing else, so at any period there is far more
 * moving off screen than on it. Saying only "3 packages rising" beside a picture of a frame going up
 * would let a viewer take the structure for the job.
 */
export function risingMoreLabel(risingMore: number, projectMoving: number): string {
  const wider = `${projectMoving.toLocaleString()} cost centres moving across the project`;
  return risingMore > 0 ? `+${risingMore} more on the model · ${wider}` : wider;
}

/** What share of the bill the elements on screen actually carry. */
export function modelScopeLabel(onModelBac: number, bac: number, currency = "AED"): string {
  const share = bac > 0 ? Math.round((100 * onModelBac) / bac) : 0;
  return `The centres on screen carry ${millionsBare(onModelBac)} of the `
    + `${millions(bac, currency)} bill — ${share}% of it`;
}

export const projectHeading = (period: number, maxPeriod: number) =>
  `${PROJECT_LABEL} · period ${period} of ${maxPeriod}`;

/** A labelled figure in the bottom strip. Three lines so the number never travels without its unit. */
export interface ReadoutCell {
  label: string;
  value: string;
  note: string;
}

/**
 * The money-and-programme strip.
 *
 * Project-wide, not model-scoped — the picture is 12% of the bill and the position is all of it.
 * Every figure is the one the EVM tab shows for the same period, because both come from the same
 * sum over the same rows; see `buildVideoStats.totalsOf`.
 */
export function budgetCells(readout: FrameReadout, currency = "AED"): ReadoutCell[] {
  const t = readout.totals;
  return [
    {
      label: "Earned value",
      value: `${millionsBare(t.ev)} of ${millions(t.bac, currency)}`,
      note: `${pct(t.earnedPct, 0)} of the bill earned`,
    },
    {
      label: "Actual cost",
      value: `${millions(t.ac, currency)} · CPI ${ratio(t.cpi)}`,
      note: costNote(t.cv),
    },
    {
      label: "Forecast at completion",
      value: millions(t.eac, currency),
      note: varianceNote(t.vac),
    },
    {
      label: "Schedule",
      value: `${pct(t.earnedPct, 0)} complete vs ${pct(t.plannedPct, 0)} planned`,
      note: `SPI ${ratio(t.spi)} · ${paceNote(t.spi)}`,
    },
  ];
}

const costNote = (cv: number) => {
  if (cv < 0) return `${millionsBare(-cv)} more spent than earned`;
  if (cv > 0) return `${millionsBare(cv)} earned above what it cost`;
  return "spend exactly matches earned value";
};

const varianceNote = (vac: number) => {
  if (vac < 0) return `${millionsBare(-vac)} over the budget`;
  if (vac > 0) return `${millionsBare(vac)} under the budget`;
  return "forecast lands on the budget";
};

const paceNote = (spi: number | null) => {
  if (spi == null) return "nothing planned yet";
  if (spi < 0.995) return "behind plan";
  return spi > 1.005 ? "ahead of plan" : "on plan";
};
