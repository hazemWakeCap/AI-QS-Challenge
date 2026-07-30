import * as THREE from "three";
import * as FRAGS from "@thatopen/fragments";
import type { CostCentreEvm, ZoneCost } from "../api/client";
import { colorFor, colorForCentreAlert, type PaintMode } from "./costPaint";
import { centresFor, worstAlert, type ElementMapIndex } from "./ifcElementMap";
import type { SequenceFrame } from "./ifcSequence";
import type { ZoneMapResult } from "./ifcZoneMap";
import { settle, type Viewer } from "./viewer";

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

  // The renderer draws every frame on its own; what is stale after a mutation is the model data on
  // the worker, which is what this waits for.
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
 * How firmly a sequence element is drawn.
 *
 * Opacity is the only thing that separates measured work from projected work, and that is deliberate:
 * hue already means cost performance, and giving "forecast" its own colour would either collide with
 * that scale or invent a verdict the data does not support. So an AMBER centre stays amber whether
 * its work is reported or projected — what changes is how solid it looks.
 *
 * `Shell` sits above {@link UNPLACED_OPACITY} so the never-priced ghosts remain the faintest thing on
 * screen. The scope gap is a harder fact than any projection and must not be outshouted by one.
 */
const SEQUENCE_OPACITY = {
  /** Reported, or projected to stand even on the pessimistic end. */
  solid: 1,
  /** Projected to stand at the median, but not at the pessimistic end. */
  likely: 0.7,
  /** Inside the band between median and optimistic — may or may not be there by this period. */
  shell: 0.3,
} as const;

type SequenceWeight = keyof typeof SEQUENCE_OPACITY;

/**
 * Draws one frame of the construction sequence.
 *
 * Elements that have not been reached yet are hidden outright rather than ghosted, so the building
 * genuinely rises instead of fading in — a half-transparent column reads as a design option, not as
 * work that has not happened.
 *
 * Past the last reported period the frame carries a band, and the band is drawn rather than described:
 * work the projection is confident about stays solid, work only its optimistic end reaches is
 * translucent. A QS looking at period 18 can see which parts of that building are a claim and which
 * are a hope, without reading a caption.
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
  /**
   * Wait until the model has genuinely finished redrawing before resolving.
   *
   * The exporter needs this — a captured frame must be the frame it asked for. Interactive playback
   * does not: settling costs a full model sweep, and a viewer that is a beat behind is a far better
   * experience than one that advances once a second.
   */
  waitForSettle = false,
): Promise<void> {
  // Only the difference is applied.
  //
  // This matters far more than it looks. Inside Fragments, every setVisible/setColor/setOpacity
  // reaches `updateVirtualMeshes` -> `restart()`, which throws away the worker's progressive sweep
  // over the WHOLE model and begins again. The cost of a mutation is therefore proportional to the
  // model, not to how much changed — so the winning move is to issue as few calls as possible, not
  // to touch as few elements as possible.
  //
  // The diff is keyed on STYLE (colour + weight), not colour alone. That is what lets an element
  // crossing from the uncertain shell into confidently-built cost one `highlight` — the same call an
  // alert change already cost — rather than needing a second mechanism of its own.
  const appear = new Map<number, number>();   // localId → style key
  const restyle = new Map<number, number>();
  const vanish: number[] = [];

  for (const localId of visibleIn(frame)) {
    const style = styleKeyFor(frame, localId);
    if (previous === null || !isVisible(previous, localId)) {
      appear.set(localId, style);
    } else if (styleKeyFor(previous, localId) !== style) {
      restyle.set(localId, style);
    }
  }

  if (previous) {
    for (const localId of visibleIn(previous)) {
      if (!isVisible(frame, localId)) vanish.push(localId);
    }
  }

  const first = previous === null;
  if (!first && appear.size === 0 && restyle.size === 0 && vanish.length === 0) return;

  // Nothing below reaches the worker until `frozen` is cleared, so the whole frame lands as one
  // batch rather than as a dozen separate whole-model restarts.
  model.frozen = true;
  try {
    if (first) {
      const hidden = index.mappedLocalIds.filter((id) => !isVisible(frame, id));
      if (hidden.length > 0) model.setVisible(hidden, false);

      // Never priced, so never built — held on screen throughout as the scope with no money behind it.
      if (index.unmappedLocalIds.length > 0) {
        model.setVisible(index.unmappedLocalIds, true);
        model.highlight(index.unmappedLocalIds, ghostStyle());
      }
    }

    if (vanish.length > 0) model.setVisible(vanish, false);

    // One `highlight` instead of a setColor + setOpacity pair: both are wrappers over the same
    // worker call, so pairing them doubled the restarts for no gain.
    for (const [style, ids] of groupByStyle(appear)) {
      model.setVisible(ids, true);
      model.highlight(ids, styleFromKey(style));
    }
    for (const [style, ids] of groupByStyle(restyle)) {
      model.highlight(ids, styleFromKey(style));
    }
  } finally {
    model.frozen = false;
  }

  // Measured on the bundled model: `update(false)` is a 3 ms median, so applying a frame is cheap.
  // Settling is not — it waits for a whole-model sweep — which is exactly why it is opt-in.
  if (waitForSettle) await settle(viewer, model);
  else await viewer.fragments.core.update(false);
}

/** Every element the frame puts on screen, built or merely possible. */
function visibleIn(frame: SequenceFrame): Iterable<number> {
  return frame.shell.size === 0 ? frame.built : [...frame.built, ...frame.shell];
}

function isVisible(frame: SequenceFrame, localId: number): boolean {
  return frame.built.has(localId) || frame.shell.has(localId);
}

/** How firmly one element should read in one frame. */
function weightFor(frame: SequenceFrame, localId: number): SequenceWeight {
  if (frame.shell.has(localId)) return "shell";
  // `confident` is empty for measured frames, where every built element is solid by definition.
  if (frame.tier === "measured" || frame.confident.has(localId)) return "solid";
  return "likely";
}

/**
 * Colour and weight packed into one integer, so the diff can compare styles with `!==` and the
 * grouping can bucket them in a plain Map.
 *
 * Colours are 24-bit, so shifting left by two and packing the weight below is lossless.
 */
const WEIGHTS: readonly SequenceWeight[] = ["solid", "likely", "shell"];

function styleKeyFor(frame: SequenceFrame, localId: number): number {
  const color = colorForCentreAlert(frame.alertByLocalId.get(localId));
  return (color << 2) | WEIGHTS.indexOf(weightFor(frame, localId));
}

function styleFromKey(key: number): FRAGS.MaterialDefinition {
  const opacity = SEQUENCE_OPACITY[WEIGHTS[key & 0b11]];
  return {
    color: new THREE.Color(key >>> 2),
    renderedFaces: FRAGS.RenderedFaces.TWO,
    opacity,
    transparent: opacity < 1,
  };
}

/** localId → style, inverted into style → localIds so each distinct style costs one call. */
function groupByStyle(byLocalId: Map<number, number>): Map<number, number[]> {
  const out = new Map<number, number[]>();
  for (const [localId, style] of byLocalId) {
    const bucket = out.get(style);
    if (bucket) bucket.push(localId);
    else out.set(style, [localId]);
  }
  return out;
}

const ghostStyle = (): FRAGS.MaterialDefinition => ({
  color: new THREE.Color(UNPLACED),
  renderedFaces: FRAGS.RenderedFaces.TWO,
  opacity: UNPLACED_OPACITY,
  transparent: true,
});

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
