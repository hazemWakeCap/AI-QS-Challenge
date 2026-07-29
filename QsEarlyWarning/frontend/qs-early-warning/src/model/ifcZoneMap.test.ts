import { describe, expect, it } from "vitest";
import type { ClassMeasurement } from "./ifcMeasure";
import { mapToZones } from "./ifcZoneMap";

/**
 * The zone map is the step that decides what a model is allowed to claim about a cost plan, so the
 * failure worth testing for is not "does it place elements" but "does it over-place them". A rule
 * set that flatters itself is worse than one that admits it reached nothing.
 */

/** A class measurement with only the fields the zone map reads. */
function cls(ifcClass: string, idsByStorey: Record<string, number[]>): ClassMeasurement {
  const byStorey: Record<string, number> = {};
  for (const [storey, ids] of Object.entries(idsByStorey)) byStorey[storey] = ids.length;
  return {
    ifcClass,
    idsByStorey,
    byStorey,
    elementCount: Object.values(idsByStorey).reduce((n, ids) => n + ids.length, 0),
    volumeCount: 0, volume: 0, areaCount: 0, area: 0,
  };
}

const ZONES = ["BASEMENT", "STRUCTURE", "FLOORS-ALL", "EXTERNAL-FACADE", "MEP-RISERS"];

describe("mapToZones", () => {
  it("places elements by the storey they actually sit on, not by their class", () => {
    // The regression the module's docblock records: this model has a below-ground level, and an
    // earlier version tested the storey against the whole class — so the below-ground rule fired
    // for every slab and reported 100% placed.
    const measured = [
      cls("IFCSLAB", { "Sub Level": [1, 2], "Level 1": [3, 4, 5], "Level 2": [6, 7] }),
    ];

    const result = mapToZones(measured, ["Sub Level", "Level 1", "Level 2"], ZONES);

    const basement = result.matched.find((m) => m.zoneCode === "BASEMENT");
    const floors = result.matched.find((m) => m.zoneCode === "FLOORS-ALL");

    expect(basement?.localIds.sort()).toEqual([1, 2]);
    expect(floors?.localIds.sort()).toEqual([3, 4, 5, 6, 7]);
    // The whole point: the below-ground rule must NOT have swallowed the above-ground slabs.
    expect(basement?.elementCount).toBe(2);
  });

  it("reports a match rate below 1 when a class has no rule", () => {
    const measured = [
      cls("IFCCOLUMN", { "Level 1": [1, 2] }),
      cls("IFCFURNISHINGELEMENT", { "Level 1": [3, 4, 5, 6] }),
    ];

    const result = mapToZones(measured, ["Level 1"], ZONES);

    expect(result.matchedElements).toBe(2);
    expect(result.totalElements).toBe(6);
    expect(result.matchRate).toBeCloseTo(2 / 6);
    expect(result.unmatched).toEqual([{ ifcClass: "IFCFURNISHINGELEMENT", elementCount: 4 }]);
  });

  it("hands back every unplaced element's id so 'not placed' can be drawn, not just counted", () => {
    const measured = [cls("IFCFURNISHINGELEMENT", { "Level 1": [7, 8, 9] })];

    const result = mapToZones(measured, ["Level 1"], ZONES);

    expect(result.unmatchedLocalIds.sort()).toEqual([7, 8, 9]);
  });

  it("accounts for every element exactly once across placed and unplaced", () => {
    const measured = [
      cls("IFCSLAB", { "Sub Level": [1], "Level 1": [2, 3] }),
      cls("IFCCOLUMN", { "Level 1": [4, 5] }),
      cls("IFCFURNISHINGELEMENT", { "(none)": [6] }),
    ];

    const result = mapToZones(measured, ["Sub Level", "Level 1"], ZONES);

    const placed = result.matched.flatMap((m) => m.localIds);
    const all = [...placed, ...result.unmatchedLocalIds].sort();

    expect(all).toEqual([1, 2, 3, 4, 5, 6]);
    expect(new Set(all).size).toBe(6); // no element painted twice
    expect(result.matchedElements + result.unmatchedLocalIds.length).toBe(result.totalElements);
  });

  it("reports the cost-plan zones the model contributed nothing to", () => {
    // The direction teams forget: a structural model carries no MEP or façade, and a match rate
    // that only looked at the model would never say so.
    const measured = [cls("IFCCOLUMN", { "Level 1": [1] })];

    const result = mapToZones(measured, ["Level 1"], ZONES);

    expect(result.zonesWithNoGeometry).toContain("MEP-RISERS");
    expect(result.zonesWithNoGeometry).toContain("EXTERNAL-FACADE");
    expect(result.zonesWithNoGeometry).not.toContain("STRUCTURE");
  });

  it("matches no rule with a storey condition when the element sits in no storey", () => {
    const measured = [cls("IFCSLAB", { "(none)": [1, 2] })];

    const result = mapToZones(measured, [], ZONES);

    // Falls through the below-ground rule to the unconditional "suspended floor slab" rule.
    expect(result.matched.find((m) => m.zoneCode === "BASEMENT")).toBeUndefined();
    expect(result.matched.find((m) => m.zoneCode === "FLOORS-ALL")?.elementCount).toBe(2);
  });
});
