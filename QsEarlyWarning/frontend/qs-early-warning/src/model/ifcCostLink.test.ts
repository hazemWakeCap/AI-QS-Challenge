import { describe, expect, it } from "vitest";
import { buildCostLinks } from "./ifcCostLink";
import type { ModelMeasurement } from "./ifcMeasure";
import type { ZoneMapResult } from "./ifcZoneMap";

/**
 * The tiering exists to stop a rule's inference being mistaken for the model's own evidence, so
 * these tests are mostly about what must NOT be promoted to Direct.
 */

function measurement(psetText: Record<number, string[]>): ModelMeasurement {
  return {
    byClass: [],
    psetTextByLocalId: new Map(Object.entries(psetText).map(([id, v]) => [Number(id), v])),
    report: {
      totalElements: 0, measuredElements: 0, unplacedElements: 0,
      baseQuantitiesEmpty: true, quantityKeysSeen: [], storeys: [],
    },
  };
}

function zoneMap(placed: Record<string, number[]>, unmatched: number[] = []): ZoneMapResult {
  const matched = Object.entries(placed).map(([zoneCode, localIds]) => ({
    zoneCode, localIds, elementCount: localIds.length, ifcClasses: ["IFCSLAB"],
  }));
  const matchedElements = matched.reduce((n, m) => n + m.elementCount, 0);
  const totalElements = matchedElements + unmatched.length;
  return {
    matched,
    unmatched: [],
    unmatchedLocalIds: unmatched,
    matchedElements,
    totalElements,
    matchRate: totalElements > 0 ? matchedElements / totalElements : 0,
    zonesWithNoGeometry: [],
  };
}

const VOCAB = { zoneCodes: ["STRUCTURE", "FLOORS-ALL"], centreCodes: ["BCC-1021", "PKG-STR"] };

describe("buildCostLinks", () => {
  it("promotes an element to Direct when its own properties name a zone", () => {
    const result = buildCostLinks(
      measurement({ 1: ["floors-all"] }),
      zoneMap({ "FLOORS-ALL": [1, 2] }),
      VOCAB,
    );

    expect(result.tierByLocalId.get(1)).toBe("Direct");
    expect(result.tierByLocalId.get(2)).toBe("Grouped");
    expect(result.directCount).toBe(1);
    expect(result.groupedCount).toBe(1);
  });

  it("does not promote prose that merely contains a code", () => {
    // A substring rule would read this as 0.9 evidence. It is a description.
    const result = buildCostLinks(
      measurement({ 1: ["concrete to floors, all levels"] }),
      zoneMap({ "FLOORS-ALL": [1] }),
      VOCAB,
    );

    expect(result.tierByLocalId.get(1)).toBe("Grouped");
    expect(result.directCount).toBe(0);
  });

  it("normalises case and separators, so FLOORS ALL and floors-all are the same code", () => {
    const result = buildCostLinks(
      measurement({ 1: ["Floors All"], 2: ["FLOORS_ALL"] }),
      zoneMap({ "FLOORS-ALL": [1, 2] }),
      VOCAB,
    );

    expect(result.directCount).toBe(2);
  });

  it("reports zero Direct — and full totals — for a model carrying no codes at all", () => {
    // The realistic case: a structural Revit export has no cost codes anywhere. The result must be
    // an honest zero rather than an empty or broken report.
    const result = buildCostLinks(
      measurement({ 1: ["c40/50"], 2: ["precast"] }),
      zoneMap({ STRUCTURE: [1, 2] }, [3, 4]),
      VOCAB,
    );

    expect(result.directCount).toBe(0);
    expect(result.groupedCount).toBe(2);
    expect(result.noneCount).toBe(2);
    expect(result.codeCarryingElements).toBe(0);
  });

  it("counts a cost-centre code as cost-awareness without letting it place the element", () => {
    // A centre id says the model knows about this cost plan, but there is no centre → zone lookup
    // to paint it through, so it must not become a Direct zone link.
    const result = buildCostLinks(
      measurement({ 1: ["bcc-1021"] }),
      zoneMap({ STRUCTURE: [1] }),
      VOCAB,
    );

    expect(result.tierByLocalId.get(1)).toBe("Grouped");
    expect(result.directCount).toBe(0);
    expect(result.codeCarryingElements).toBe(1);
    expect(result.codesFound).toContain("BCC-1021");
  });

  it("never lets a rule downgrade an element that carries its own code", () => {
    const result = buildCostLinks(
      measurement({ 5: ["structure"] }),
      zoneMap({ STRUCTURE: [5] }),
      VOCAB,
    );

    expect(result.tierByLocalId.get(5)).toBe("Direct");
  });

  it("ignores codes too short to be identifiers", () => {
    const result = buildCostLinks(
      measurement({ 1: ["b2"] }),
      zoneMap({ STRUCTURE: [1] }),
      { zoneCodes: ["B2"], centreCodes: [] },
    );

    expect(result.directCount).toBe(0);
  });

  it("tiers and the unplaced remainder account for every element", () => {
    const result = buildCostLinks(
      measurement({ 1: ["structure"] }),
      zoneMap({ STRUCTURE: [1, 2, 3] }, [4, 5]),
      VOCAB,
    );

    expect(result.directCount + result.groupedCount + result.noneCount).toBe(result.totalElements);
  });
});
