import type * as FRAGS from "@thatopen/fragments";
import type { CostCentreEvm, ElementMap, MappedElement, MappedItem } from "../api/client";

/**
 * Resolves the authored element register against a loaded model.
 *
 * <b>What this makes possible.</b> The register binds a GlobalId to BOQ items; the model knows
 * elements by `localId`. Joining the two is the whole trick — once an on-screen element has a
 * localId, and that localId has a bill item, and that bill item IS a cost centre's `WBS_Code`, a
 * click on geometry reaches twelve periods of earned value without anything further being invented.
 *
 * The GlobalId → localId hop uses the model's own index (`getLocalIdsByGuids`), so an element that
 * is in the register but not in the loaded file simply resolves to nothing rather than mismatching.
 */

export interface ResolvedElement {
  localId: number;
  element: MappedElement;
  /** The bill items this element consumes, in register order. Empty when the bill prices none. */
  items: MappedItem[];
}

export interface ElementMapIndex {
  byLocalId: Map<number, ResolvedElement>;
  itemByRef: Map<string, MappedItem>;
  /** localIds the register bound to at least one bill item — what gets painted. */
  mappedLocalIds: number[];
  /** localIds present in the register with no bill item — the scope gap, drawn as ghosts. */
  unmappedLocalIds: number[];
  /**
   * How many elements rest on each confidence level.
   *
   * Replaces the property-set link tiers on this model: the register IS the linking mechanism here,
   * and a pset scan finds nothing because a Revit structural export carries no cost codes.
   */
  confidenceBands: { confidence: number; elementCount: number; label: string }[];
  map: ElementMap;
  /** Elements in the register that the loaded model does not contain. Zero on the bundled pair. */
  notInModel: number;
}

/** How a confidence number reads to a QS. Kept next to the bands so the wording stays in one place. */
function bandLabel(confidence: number): string {
  if (confidence >= 0.9) return "Declared by element class";
  if (confidence > 0) return "Inferred from storey";
  return "No bill item";
}

export async function buildElementIndex(
  model: FRAGS.FragmentsModel,
  map: ElementMap,
): Promise<ElementMapIndex> {
  const itemByRef = new Map(map.items.map((i) => [i.boqItemRef, i]));

  const guids = map.elements.map((e) => e.globalId);
  const localIds = await model.getLocalIdsByGuids(guids);

  const byLocalId = new Map<number, ResolvedElement>();
  const mappedLocalIds: number[] = [];
  const unmappedLocalIds: number[] = [];
  const bands = new Map<number, number>();
  let notInModel = 0;

  for (let i = 0; i < map.elements.length; i++) {
    const localId = localIds[i];
    const element = map.elements[i];

    if (typeof localId !== "number") {
      // In the register, absent from this file. Counted rather than silently dropped — it is the
      // signal that the register and the loaded model have drifted apart.
      notInModel++;
      continue;
    }

    const items = element.boqItemRefs
      .map((ref) => itemByRef.get(ref))
      .filter((it): it is MappedItem => !!it);

    byLocalId.set(localId, { localId, element, items });
    (items.length > 0 ? mappedLocalIds : unmappedLocalIds).push(localId);
    bands.set(element.confidence, (bands.get(element.confidence) ?? 0) + 1);
  }

  const confidenceBands = [...bands.entries()]
    .map(([confidence, elementCount]) => ({
      confidence,
      elementCount,
      label: bandLabel(confidence),
    }))
    .sort((a, b) => b.confidence - a.confidence);

  return { byLocalId, itemByRef, mappedLocalIds, unmappedLocalIds, confidenceBands, map, notInModel };
}

/**
 * The cost centres an element's bill items belong to.
 *
 * An element usually touches more than one — a slab is concrete AND its soffit formwork, and those
 * are two separately tracked centres with their own earned value.
 */
export function centresFor(
  resolved: ResolvedElement,
  centres: readonly CostCentreEvm[],
): CostCentreEvm[] {
  const wanted = new Set(resolved.items.map((i) => i.bccId).filter((b): b is string => !!b));
  return centres.filter((c) => wanted.has(c.bccId));
}

/** Alert levels ordered by how much they should worry a QS. Used to pick what an element reads as. */
const SEVERITY: Record<string, number> = {
  AMBER: 4,
  GREEN: 2,
  CLOSED: 1,
  "NOT STARTED": 0,
};

/**
 * The alert an element should read as, given every centre it touches.
 *
 * Takes the worst, not the average: a slab whose concrete is on budget and whose formwork is
 * drifting is a slab with a problem, and averaging the two would hide it — the same aggregation
 * trap the zone map's MIXED colour exists to avoid.
 */
export function worstAlert(centres: readonly CostCentreEvm[]): string | null {
  let worst: string | null = null;
  let rank = -1;
  for (const c of centres) {
    const r = SEVERITY[c.alertLevel?.toUpperCase() ?? ""] ?? 3;
    if (r > rank) {
      rank = r;
      worst = c.alertLevel;
    }
  }
  return worst;
}
