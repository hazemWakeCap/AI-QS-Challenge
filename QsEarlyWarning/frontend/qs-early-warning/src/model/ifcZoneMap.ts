import type { ClassMeasurement } from "./ifcMeasure";

/**
 * Classifies a loaded IFC's elements into Tower X's cost zones.
 *
 * <b>What this demonstrates, and what it does not.</b> The mechanism is real: element class plus
 * storey is how a model gets bound to contractual cost buckets when the model carries no cost
 * codes of its own. What it is NOT is a statement about the loaded building — the sample IFC is a
 * school and Tower X is a tower, so a matched element means "this kind of element would map here",
 * never "this element belongs to that budget".
 *
 * The output that matters is therefore the <b>match rate</b>, not the mapping: how much of the
 * model a rule set like this can actually place, and what it leaves behind.
 */

export interface ZoneMapRule {
  ifcClass: string;
  /** Storey-name test; when absent the rule applies at any level. */
  storey?: (name: string) => boolean;
  zoneCode: string;
  rationale: string;
}

const isBelowGround = (name: string) => /sub|basement|b[0-9]|below/i.test(name);
const isRoof = (name: string) => /roof/i.test(name);

/**
 * Declared rules, in priority order. Deliberately narrow: a rule that guessed would inflate the
 * match rate, which is the one number this whole exercise exists to report honestly.
 */
export const ZONE_RULES: ZoneMapRule[] = [
  { ifcClass: "IFCFOOTING", zoneCode: "BASEMENT", rationale: "Foundations sit in the substructure." },
  { ifcClass: "IFCPILE", zoneCode: "BASEMENT", rationale: "Piling is substructure work." },
  { ifcClass: "IFCSLAB", storey: isBelowGround, zoneCode: "BASEMENT", rationale: "Slab below ground." },
  { ifcClass: "IFCSLAB", storey: isRoof, zoneCode: "FLOORS-ALL", rationale: "Roof slab is priced with the floor plates." },
  { ifcClass: "IFCSLAB", zoneCode: "FLOORS-ALL", rationale: "Suspended floor slab." },
  { ifcClass: "IFCCOLUMN", zoneCode: "STRUCTURE", rationale: "Frame member." },
  { ifcClass: "IFCBEAM", zoneCode: "STRUCTURE", rationale: "Frame member." },
  { ifcClass: "IFCMEMBER", zoneCode: "STRUCTURE", rationale: "Frame member." },
  { ifcClass: "IFCREINFORCINGBAR", zoneCode: "STRUCTURE", rationale: "Reinforcement to the frame." },
  { ifcClass: "IFCWALL", zoneCode: "STRUCTURE", rationale: "Structural wall / core." },
  { ifcClass: "IFCWALLSTANDARDCASE", zoneCode: "STRUCTURE", rationale: "Structural wall / core." },
  { ifcClass: "IFCCURTAINWALL", zoneCode: "EXTERNAL-FACADE", rationale: "Envelope." },
  { ifcClass: "IFCPLATE", zoneCode: "EXTERNAL-FACADE", rationale: "Envelope panel." },
  { ifcClass: "IFCCOVERING", zoneCode: "FLOORS-B2-RF", rationale: "Interior finish." },
];

export interface ZoneMatch {
  zoneCode: string;
  elementCount: number;
  ifcClasses: string[];
}

export interface ZoneMapResult {
  matched: ZoneMatch[];
  /** Classes no rule placed, with their element counts. */
  unmatched: { ifcClass: string; elementCount: number }[];
  matchedElements: number;
  totalElements: number;
  /** 0..1 — the share of the model a rule placed. The headline. */
  matchRate: number;
  /** Zones in the cost map that the model contributed nothing to. */
  zonesWithNoGeometry: string[];
}

/**
 * Applies the rules per (class, storey), never per class.
 *
 * Testing a storey condition against the whole class is what an earlier version did, and it was
 * badly wrong: because this model has a "Sub Level", the below-ground slab rule fired for ALL 299
 * slabs and reported a flattering 100% placed. Elements are placed by the level they actually sit
 * on; those in no storey only match rules that carry no storey condition.
 */
export function mapToZones(
  byClass: ClassMeasurement[],
  _storeys: string[],
  costMapZones: string[],
): ZoneMapResult {
  const matches = new Map<string, ZoneMatch>();
  const unmatchedByClass = new Map<string, number>();
  let matchedElements = 0;
  let totalElements = 0;

  for (const c of byClass) {
    totalElements += c.elementCount;

    for (const [storey, count] of Object.entries(c.byStorey)) {
      const rule = ZONE_RULES.find(
        (r) => r.ifcClass === c.ifcClass && (!r.storey || (storey !== "(none)" && r.storey(storey))),
      );

      if (!rule) {
        unmatchedByClass.set(c.ifcClass, (unmatchedByClass.get(c.ifcClass) ?? 0) + count);
        continue;
      }

      const existing = matches.get(rule.zoneCode);
      if (existing) {
        existing.elementCount += count;
        if (!existing.ifcClasses.includes(c.ifcClass)) existing.ifcClasses.push(c.ifcClass);
      } else {
        matches.set(rule.zoneCode, {
          zoneCode: rule.zoneCode,
          elementCount: count,
          ifcClasses: [c.ifcClass],
        });
      }
      matchedElements += count;
    }
  }

  const unmatched = [...unmatchedByClass.entries()].map(([ifcClass, elementCount]) => ({
    ifcClass,
    elementCount,
  }));

  const matched = [...matches.values()].sort((a, b) => b.elementCount - a.elementCount);

  return {
    matched,
    unmatched: unmatched.sort((a, b) => b.elementCount - a.elementCount),
    matchedElements,
    totalElements,
    matchRate: totalElements > 0 ? matchedElements / totalElements : 0,
    // The other direction, and the one teams forget: a structural model matches none of the MEP,
    // finishes or landscaping budget, and saying so is the point.
    zonesWithNoGeometry: costMapZones.filter((z) => !matches.has(z)).sort(),
  };
}
