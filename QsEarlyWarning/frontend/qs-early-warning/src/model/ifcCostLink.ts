import type { LinkTier } from "./ifcPaint";
import type { ModelMeasurement } from "./ifcMeasure";
import type { ZoneMapResult } from "./ifcZoneMap";

/**
 * Grades how firmly each element is linked to a cost zone.
 *
 * A class+storey rule and a cost code written into the model are both "links", and treating them as
 * equally true is the quiet mistake this module exists to stop. A slab placed by the rule
 * "suspended floor slab → FLOORS-ALL" is an inference about a category. A slab whose property set
 * literally says `FLOORS-ALL` is a statement by whoever authored the model. The second is evidence;
 * the first is a reasonable guess, and the difference should survive all the way to the picture.
 *
 * Two tiers, deterministic, never inferred by a model:
 *
 * - <b>Direct</b> (0.9) — a property value on the element matches a zone code exactly. The element
 *   says where it belongs and no rule had to decide for it.
 * - <b>Grouped</b> (0.4) — the class + storey rule placed it. True of the category, not the element.
 *
 * Anything else is <b>None</b>, and stays visibly none.
 */

/** Confidence attached to each tier. Declared here so the UI and the paint agree on one number. */
export const TIER_CONFIDENCE: Record<LinkTier, number> = {
  Direct: 0.9,
  Grouped: 0.4,
};

export interface CostVocabulary {
  /** Zone codes from the cost map — the only codes that can place an element on the picture. */
  zoneCodes: readonly string[];
  /**
   * Cost-centre and package identifiers. A model carrying these is cost-aware even when it names no
   * zone, so they are counted and reported — but they cannot paint, because the API exposes no
   * centre → zone lookup to resolve them through.
   */
  centreCodes: readonly string[];
}

export interface CostLinkResult {
  /** localId → the tier its zone link was established at. Feeds the paint directly. */
  tierByLocalId: Map<number, LinkTier>;
  /** Elements whose own property sets named a zone. */
  directCount: number;
  /** Elements placed only by a class + storey rule. */
  groupedCount: number;
  /** Elements no tier reached. */
  noneCount: number;
  totalElements: number;
  /**
   * Elements carrying a recognised cost identifier of any kind, zone or centre.
   *
   * Reported separately from `directCount` because it answers a different question: not "did this
   * link" but "was this model authored with cost in mind at all". Zero is a real and common answer.
   */
  codeCarryingElements: number;
  /** The distinct recognised codes actually found, for the UI to show rather than assert. */
  codesFound: string[];
}

/**
 * Exact match after normalising case, separators and whitespace.
 *
 * Deliberately not a substring test. `FLOORS-ALL` appearing inside a description like "concrete to
 * floors, all levels" is prose, not a code, and a substring rule would promote it to 0.9 evidence.
 * A code that has to be found by fuzzy search is not a code you should be pricing off.
 */
const normalise = (s: string) => s.trim().toUpperCase().replace(/[\s_]+/g, "-");

/** Codes shorter than this match too much by accident to be trusted as identifiers. */
const MIN_CODE_LENGTH = 3;

export function buildCostLinks(
  measurement: ModelMeasurement,
  zoneMap: ZoneMapResult,
  vocabulary: CostVocabulary,
): CostLinkResult {
  const zoneByCode = new Map<string, string>();
  for (const code of vocabulary.zoneCodes) {
    if (code.length >= MIN_CODE_LENGTH) zoneByCode.set(normalise(code), code);
  }
  const centreCodes = new Set(
    vocabulary.centreCodes.filter((c) => c.length >= MIN_CODE_LENGTH).map(normalise),
  );

  const tierByLocalId = new Map<number, LinkTier>();
  const codesFound = new Set<string>();
  let codeCarryingElements = 0;

  // Grouped first: every element a rule placed starts at the weaker tier, and a direct hit
  // overwrites it below. Doing it the other way round would let a rule downgrade real evidence.
  for (const match of zoneMap.matched) {
    for (const id of match.localIds) tierByLocalId.set(id, "Grouped");
  }

  for (const [localId, values] of measurement.psetTextByLocalId) {
    let carriesCode = false;

    for (const value of values) {
      const key = normalise(value);

      const zone = zoneByCode.get(key);
      if (zone) {
        tierByLocalId.set(localId, "Direct");
        codesFound.add(zone);
        carriesCode = true;
        continue;
      }

      if (centreCodes.has(key)) {
        // Cost-aware, but not placeable — see CostVocabulary.centreCodes.
        codesFound.add(value.toUpperCase());
        carriesCode = true;
      }
    }

    if (carriesCode) codeCarryingElements++;
  }

  let directCount = 0;
  let groupedCount = 0;
  for (const tier of tierByLocalId.values()) {
    if (tier === "Direct") directCount++;
    else groupedCount++;
  }

  const totalElements = zoneMap.totalElements;

  return {
    tierByLocalId,
    directCount,
    groupedCount,
    noneCount: Math.max(0, totalElements - directCount - groupedCount),
    totalElements,
    codeCarryingElements,
    codesFound: [...codesFound].sort(),
  };
}
