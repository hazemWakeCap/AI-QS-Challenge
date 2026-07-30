import * as THREE from "three";
import type { ZoneCost } from "../api/client";
import type { GeneratedTower } from "./towerGenerator";
import type { Viewer } from "./viewer";

/**
 * Colour policy for the massing — which cost state reads as which colour.
 *
 * Colours come from the app's existing semantic tokens (--good / --warn / --bad), which were
 * already checked for colour-vision safety, so the model reads the same way as the tables.
 * The generator applies these at build time; see towerGenerator for why they are not repainted.
 */

/** GREEN — CPI at or above the 0.95 AMBER threshold. */
const GOOD = 0x22a56a;
/** AMBER — CPI below 0.95. */
const WARN = 0xd99a1c;
/** A zone carrying AMBER centres while its own rollup still reads green. */
const MIXED = 0xc98a3c;
/** Too little money spent to judge — deliberately not green. */
const UNKNOWN = 0x9aa4b8;
/** Nothing started. */
const DORMANT = 0xc3cad6;

/** The app's accent blue — the selected zone, so selection never reads as a cost verdict. */
const SELECTION_COLOR = 0x2f6fe0;

export type PaintMode = "cpi" | "exposure";

/**
 * Colour for one zone.
 *
 * The `cpi` mode paints the zone's own rollup. It has a known blind spot: FLOORS-ALL reads GREEN
 * at CPI 0.961 while holding 11 AMBER cost centres and AED 43.5M of unspent budget — aggregation
 * dilutes trouble. So a green zone that contains AMBER centres is painted MIXED rather than clean
 * green, and `exposure` mode exists to rank zones by unspent money instead.
 */
export function colorFor(zone: ZoneCost, mode: PaintMode, maxUnspent: number): number {
  if (mode === "exposure") {
    // Money still at stake, as a ramp from calm to hot. Unspent budget is what a QS can still act on.
    const t = maxUnspent > 0 ? Math.min(1, zone.unspent / maxUnspent) : 0;
    const c = new THREE.Color(GOOD).lerp(new THREE.Color(WARN), t);
    return c.getHex();
  }

  if (zone.alertLevel === "NOT_STARTED") return DORMANT;
  if (zone.alertLevel === "INSUFFICIENT_COST") return UNKNOWN;
  if (zone.alertLevel === "AMBER") return WARN;
  return zone.amberCount > 0 ? MIXED : GOOD;
}

/**
 * Colour for a single cost centre's alert level.
 *
 * Lives here rather than beside the element map so there is exactly one place that decides what
 * AMBER looks like. A zone rollup and an individual centre are different grains of the same
 * question, and they must not answer it in different colours.
 *
 * Note `NOT STARTED` carries a space in the panel data while `ZoneCost` uses `NOT_STARTED` — both
 * are handled, because a colour silently falling through to "unknown" over a separator would be
 * indistinguishable from real missing data.
 */
export function colorForCentreAlert(alertLevel: string | null | undefined): number {
  switch ((alertLevel ?? "").trim().toUpperCase().replace(/_/g, " ")) {
    case "AMBER": return WARN;
    case "GREEN": return GOOD;
    case "CLOSED": return GOOD;
    case "NOT STARTED": return DORMANT;
    default: return UNKNOWN;
  }
}

/** Legend for painting individual elements by their own cost centre. */
export function centreLegend(): LegendRow[] {
  return [
    { label: "Drifting", color: WARN, note: "this element's cost centre is AMBER" },
    { label: "On budget", color: GOOD, note: "cost centre is GREEN or closed" },
    { label: "Not started", color: DORMANT, note: "no work booked against it yet" },
    { label: "No verdict", color: UNKNOWN, note: "cost centre carries no alert level" },
  ];
}

export interface LegendRow {
  label: string;
  color: number;
  note: string;
  /** Draws the swatch at reduced opacity, for the sequence's projected weights. */
  opacity?: number;
}

/**
 * Legend for a projected period of the build sequence.
 *
 * Deliberately carries no new colour: hue means cost performance throughout the app, and a projected
 * AMBER centre is still amber. What the projection changes is how solid the work reads, so this key
 * explains the opacity scale rather than adding to the colour scale.
 */
export function forecastLegend(): LegendRow[] {
  return [
    { label: "Standing", color: UNKNOWN, note: "reported, or projected even at the pessimistic end", opacity: 1 },
    { label: "Expected", color: UNKNOWN, note: "projected to stand at the median", opacity: 0.7 },
    { label: "Might be", color: UNKNOWN, note: "inside the band — may not be there by this period", opacity: 0.3 },
  ];
}

/**
 * Legend for the active mode.
 *
 * The two modes encode completely different things — one categorical, one a continuous ramp — so
 * they cannot share a key. Showing the CPI categories while the model is ramped by unspent money
 * would label the picture with a scale it is not using.
 */
export function legendFor(mode: PaintMode): LegendRow[] {
  if (mode === "exposure") {
    return [
      { label: "Most unspent", color: WARN, note: "largest budget still to be committed" },
      { label: "Least unspent", color: GOOD, note: "little budget left in this zone" },
    ];
  }
  return [
    { label: "Drifting", color: WARN, note: "zone CPI below 0.95" },
    { label: "Amber inside", color: MIXED, note: "zone reads green, but holds AMBER centres" },
    { label: "On budget", color: GOOD, note: "zone CPI at or above 0.95" },
    { label: "Too early to judge", color: UNKNOWN, note: "under 1% of budget spent" },
    { label: "Not started", color: DORMANT, note: "no work booked" },
  ];
}

export const hex = (c: number) => `#${c.toString(16).padStart(6, "0")}`;

/**
 * Recolours the massing in place.
 *
 * Meshes own their materials, so a repaint is a colour assignment — no rebuild, no per-item API.
 * Zones the cost map does not mention keep DORMANT rather than a cost colour, so geometry can
 * never imply a verdict the data did not give.
 */
export function paintByCost(
  viewer: Viewer,
  tower: GeneratedTower,
  zones: readonly ZoneCost[],
  mode: PaintMode,
  selectedZone: string | null,
): void {
  const maxUnspent = zones.reduce((m, z) => Math.max(m, z.unspent), 0);

  const colorByZone = new Map<string, number>();
  for (const z of zones) {
    colorByZone.set(z.zoneCode, colorFor(z, mode, maxUnspent));
  }

  for (const [zoneCode, meshes] of tower.byZone) {
    const color = zoneCode === selectedZone
      ? SELECTION_COLOR
      : colorByZone.get(zoneCode) ?? DORMANT;
    for (const mesh of meshes) {
      (mesh.material as THREE.MeshLambertMaterial).color.setHex(color);
    }
  }

  // The renderer runs in AUTO mode, so it already draws every animation frame and `needsUpdate` is
  // only honoured in MANUAL. This line is therefore a no-op today — kept because the render harness
  // does switch to MANUAL, and there it is what makes the new colour appear.
  viewer.world.renderer!.needsUpdate = true;
}
