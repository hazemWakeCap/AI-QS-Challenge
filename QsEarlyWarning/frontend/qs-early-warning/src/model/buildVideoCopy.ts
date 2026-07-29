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
