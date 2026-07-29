import * as THREE from "three";
import type * as FRAGS from "@thatopen/fragments";
import type { CostCentreEvm, ZoneCost } from "../api/client";
import { colorFor, colorForCentreAlert, type PaintMode } from "./costPaint";
import { centresFor, worstAlert, type ElementMapIndex } from "./ifcElementMap";
import type { SequenceFrame } from "./ifcSequence";
import type { ZoneMapResult } from "./ifcZoneMap";
import type { Viewer } from "./viewer";

/**
 * Paints zone cost onto a real IFC.
 *
 * The massing tab colours meshes it generated itself, so a repaint there is a material assignment.
 * A real IFC carries its own item index instead, so colour goes through the model's per-item API —
 * `setColor` over the localIds a zone rule placed. That index is the whole reason this is possible
 * here and not on the generated path.
 *
 * <b>The colour policy is imported, never restated.</b> `colorFor` is the same function the massing
 * tab paints with, so a zone that reads AMBER in one 3D tab cannot read GREEN in the other. Adding a
 * second copy of the thresholds here is exactly how two views of one number start disagreeing.
 */

/**
 * Elements no zone rule placed.
 *
 * Deliberately not one of the cost colours: an unplaced element has no cost verdict, and painting it
 * anything on the CPI scale would assert one. It is drawn as a faint ghost so the share of the model
 * a rule set could NOT reach stays visible in the picture, not just in the table.
 */
const UNPLACED = 0xd7dce4;
const UNPLACED_OPACITY = 0.25;

/** Confidence in the link between an element and the cost it is being painted with. */
export type LinkTier = "Direct" | "Grouped";

/**
 * How solidly each tier is drawn.
 *
 * A `Grouped` link says "an element of this class on this storey belongs to that zone" — true often
 * enough to be useful, weak enough that it should not look like a measurement. Rendering it at
 * reduced opacity is the visual form of its 0.4 confidence: a QS can see at a glance which parts of
 * the picture are asserted and which are inferred.
 */
const TIER_OPACITY: Record<LinkTier, number> = {
  Direct: 1,
  Grouped: 0.55,
};

export interface IfcPaintPlan {
  /** localIds → the tier their zone link was established at. Absent ids paint as unplaced. */
  tierByLocalId?: Map<number, LinkTier>;
}

/**
 * Colours every placed element by its zone's cost, and ghosts the rest.
 *
 * Returns the number of elements actually painted, which is not the same as the number matched: an
 * element in a zone the cost map never mentions has no cost to show, and is ghosted rather than
 * given a colour the data did not earn.
 */
export async function paintIfcByCost(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  zoneMap: ZoneMapResult,
  zones: readonly ZoneCost[],
  mode: PaintMode,
  plan: IfcPaintPlan = {},
): Promise<number> {
  const maxUnspent = zones.reduce((m, z) => Math.max(m, z.unspent), 0);
  const costByZone = new Map(zones.map((z) => [z.zoneCode, z]));

  // Group by (colour, opacity) so the model takes a handful of batched calls rather than one per
  // element — an 8 MB structural model runs to thousands of them.
  const byStyle = new Map<string, { color: number; opacity: number; ids: number[] }>();
  const ghosts: number[] = [...zoneMap.unmatchedLocalIds];
  let painted = 0;

  for (const match of zoneMap.matched) {
    const zone = costByZone.get(match.zoneCode);
    if (!zone) {
      // Placed by a rule, but the cost plan has nothing to say about that zone at this period.
      ghosts.push(...match.localIds);
      continue;
    }

    const color = colorFor(zone, mode, maxUnspent);
    for (const id of match.localIds) {
      const opacity = TIER_OPACITY[plan.tierByLocalId?.get(id) ?? "Grouped"];
      const key = `${color}:${opacity}`;
      const bucket = byStyle.get(key) ?? { color, opacity, ids: [] };
      bucket.ids.push(id);
      byStyle.set(key, bucket);
      painted++;
    }
  }

  for (const { color, opacity, ids } of byStyle.values()) {
    model.setColor(ids, new THREE.Color(color));
    model.setOpacity(ids, opacity);
  }

  if (ghosts.length > 0) {
    model.setColor(ghosts, new THREE.Color(UNPLACED));
    model.setOpacity(ghosts, UNPLACED_OPACITY);
  }

  // The world renders on demand, so a colour change is invisible until the renderer is asked to
  // draw again — the same reason the massing tab sets needsUpdate after a repaint.
  await viewer.fragments.core.update(true);
  return painted;
}

/**
 * Paints each element by its own cost centre, rather than by the zone it falls in.
 *
 * <b>Why this supersedes the zone paint where the register exists.</b> A zone rollup answers "is
 * this part of the building in trouble" over dozens of centres at once; the register answers it for
 * the element under the cursor. On the bundled model that is the difference between three coloured
 * regions and eight separately tracked cost centres, and it is the whole point of authoring the
 * element→bill binding.
 *
 * Opacity carries the register's confidence, so a rebar bar placed by storey never looks as solid
 * as a column placed by its own class.
 *
 * Returns how many elements were painted.
 */
export async function paintIfcByCostCentre(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  index: ElementMapIndex,
  centres: readonly CostCentreEvm[],
  mode: PaintMode,
): Promise<number> {
  const maxUnspent = centres.reduce((m, c) => Math.max(m, Math.max(0, c.bac - c.ac)), 0);

  const byStyle = new Map<string, { color: number; opacity: number; ids: number[] }>();
  const ghosts: number[] = [...index.unmappedLocalIds];
  let painted = 0;

  for (const localId of index.mappedLocalIds) {
    const resolved = index.byLocalId.get(localId);
    if (!resolved) continue;

    const own = centresFor(resolved, centres);
    if (own.length === 0) {
      // Bound to a bill item, but that item's centre is not in this period's panel.
      ghosts.push(localId);
      continue;
    }

    let color: number;
    if (mode === "exposure") {
      // Ranked by the money still to be committed on the element's worst-off centre. A ranking
      // signal, not an additive one — several elements share a centre and its unspent budget.
      const unspent = Math.max(...own.map((c) => Math.max(0, c.bac - c.ac)));
      const t = maxUnspent > 0 ? Math.min(1, unspent / maxUnspent) : 0;
      color = new THREE.Color(0x22a56a).lerp(new THREE.Color(0xd99a1c), t).getHex();
    } else {
      color = colorForCentreAlert(worstAlert(own));
    }

    // The register's own confidence, not a link tier: 0.9 declared by class, 0.6 inferred by storey.
    const opacity = resolved.element.confidence >= 0.9 ? 1 : 0.55;
    const key = `${color}:${opacity}`;
    const bucket = byStyle.get(key) ?? { color, opacity, ids: [] };
    bucket.ids.push(localId);
    byStyle.set(key, bucket);
    painted++;
  }

  for (const { color, opacity, ids } of byStyle.values()) {
    model.setColor(ids, new THREE.Color(color));
    model.setOpacity(ids, opacity);
  }

  if (ghosts.length > 0) {
    model.setColor(ghosts, new THREE.Color(UNPLACED));
    model.setOpacity(ghosts, UNPLACED_OPACITY);
  }

  await viewer.fragments.core.update(true);
  return painted;
}

/**
 * Draws one frame of the construction sequence.
 *
 * Elements that have not been reached yet are hidden outright rather than ghosted, so the building
 * genuinely rises instead of fading in — a half-transparent column reads as a design option, not as
 * work that has not happened.
 *
 * Elements the bill prices nothing for never join the sequence. They stay as faint ghosts for the
 * whole run, which is the scope gap made visual: 375 beams that never get built because no item
 * ever paid for them.
 */
export async function paintSequenceFrame(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  index: ElementMapIndex,
  frame: SequenceFrame,
  previous: SequenceFrame | null,
): Promise<void> {
  // Only the difference is applied. Re-sending all ~1,100 elements every tick and forcing a full
  // refresh saturates the main thread badly enough to freeze the renderer, and almost nothing
  // changes between two frames a quarter of a period apart.
  const appear = new Map<number, number>();   // localId → colour
  const recolour = new Map<number, number>();
  const vanish: number[] = [];

  for (const localId of frame.built) {
    const color = colorForCentreAlert(frame.alertByLocalId.get(localId));
    if (!previous?.built.has(localId)) {
      appear.set(localId, color);
    } else if (colorForCentreAlert(previous.alertByLocalId.get(localId)) !== color) {
      recolour.set(localId, color);
    }
  }

  if (previous) {
    for (const localId of previous.built) {
      if (!frame.built.has(localId)) vanish.push(localId);
    }
  } else {
    // First frame of a run: everything priced starts hidden, then this frame's share appears.
    const hidden = index.mappedLocalIds.filter((id) => !frame.built.has(id));
    if (hidden.length > 0) model.setVisible(hidden, false);

    // Never priced, so never built — held on screen throughout as the scope with no money behind it.
    if (index.unmappedLocalIds.length > 0) {
      model.setVisible(index.unmappedLocalIds, true);
      model.setColor(index.unmappedLocalIds, new THREE.Color(UNPLACED));
      model.setOpacity(index.unmappedLocalIds, UNPLACED_OPACITY);
    }
  }

  if (vanish.length > 0) model.setVisible(vanish, false);

  const byColor = new Map<number, number[]>();
  for (const [localId, color] of appear) (byColor.get(color) ?? byColor.set(color, []).get(color)!).push(localId);

  for (const [color, ids] of byColor) {
    model.setVisible(ids, true);
    model.setColor(ids, new THREE.Color(color));
    model.setOpacity(ids, 1);
  }

  const byRecolour = new Map<number, number[]>();
  for (const [localId, color] of recolour)
    (byRecolour.get(color) ?? byRecolour.set(color, []).get(color)!).push(localId);
  for (const [color, ids] of byRecolour) model.setColor(ids, new THREE.Color(color));

  if (appear.size === 0 && recolour.size === 0 && vanish.length === 0 && previous) return;

  // `update(false)` rather than `update(true)`: a forced refresh re-evaluates the whole model and
  // costs enough at this frame rate to stall the renderer, and the sequence only ever changes
  // visibility and colour on geometry that is already resident.
  await viewer.fragments.core.update(false);
}

/** Restores every element the sequence hid, so leaving playback does not leave a half-built model. */
export async function showAllElements(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  index: ElementMapIndex,
): Promise<void> {
  const all = [...index.mappedLocalIds, ...index.unmappedLocalIds];
  if (all.length === 0) return;
  model.setVisible(all, true);
  await viewer.fragments.core.update(true);
}

/** Drops every applied colour and opacity, returning the model to how the exporter shipped it. */
export async function clearIfcPaint(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  zoneMap: ZoneMapResult,
): Promise<void> {
  const all = [
    ...zoneMap.matched.flatMap((m) => m.localIds),
    ...zoneMap.unmatchedLocalIds,
  ];
  if (all.length === 0) return;
  model.resetColor(all);
  model.resetOpacity(all);
  await viewer.fragments.core.update(true);
}

/** Legend row for the ghost, which the shared cost legend does not cover because it is not a cost state. */
export const unplacedLegend = {
  label: "Not placed",
  color: UNPLACED,
  note: "no zone rule reached this element",
};
