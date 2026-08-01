import { describe, expect, it, vi } from "vitest";
import * as FRAGS from "@thatopen/fragments";
import type { CostCentreEvm, ElementMap, MappedElement, MappedItem } from "../api/client";
import type { ElementMapIndex, ResolvedElement } from "./ifcElementMap";
import { paintIfcByCostCentre, paintSequenceFrame } from "./ifcPaint";
import { frameAt, type BuildSequence } from "./ifcSequence";
import type { Viewer } from "./viewer";

/**
 * How this file paints, rather than what it paints.
 *
 * The bug it exists to prevent had no visible symptom at the call site and no failing assertion
 * anywhere: the take-off tab painted its elements with `setColor`, the sequence painter then took
 * over with `highlight`, and every colour it asked for was silently discarded. The building rose
 * correctly and was the wrong colour for the rest of the session — while the video tab, which never
 * calls `setColor`, was fine. Both were "the same code".
 *
 * The cause is in Fragments, not here. `setColor` and `setOpacity` store their highlight with
 * `_explicitProps: ["color"]` / `["opacity","transparent"]`, and `getNewHighFromPast` copies every
 * explicit prop of the PAST highlight over the incoming material — so the first `setColor` wins
 * permanently and the flag accumulates rather than clearing.
 *
 * So the property worth pinning is one about which API is used, not about pixels: nothing in this
 * module may reach for `setColor`/`setOpacity`, and every highlight must spell out all four
 * properties so the next one can overwrite it cleanly. A colour assertion would not have caught it —
 * the colour passed in was right every time.
 */

/** A model that records what it was asked to do, and nothing else. */
function fakeModel() {
  const calls: { fn: string; ids: number[] | undefined; arg?: unknown }[] = [];
  return {
    calls,
    frozen: false,
    highlight: vi.fn((ids: number[] | undefined, mat: FRAGS.MaterialDefinition) => {
      calls.push({ fn: "highlight", ids, arg: mat });
    }),
    resetHighlight: vi.fn((ids?: number[]) => { calls.push({ fn: "resetHighlight", ids }); }),
    setVisible: vi.fn((ids: number[], v: boolean) => { calls.push({ fn: "setVisible", ids, arg: v }); }),
    setColor: vi.fn(() => { calls.push({ fn: "setColor", ids: undefined }); }),
    setOpacity: vi.fn(() => { calls.push({ fn: "setOpacity", ids: undefined }); }),
    resetColor: vi.fn(() => { calls.push({ fn: "resetColor", ids: undefined }); }),
    resetOpacity: vi.fn(() => { calls.push({ fn: "resetOpacity", ids: undefined }); }),
  };
}

const asModel = (m: ReturnType<typeof fakeModel>) => m as unknown as FRAGS.FragmentsModel;

const viewer = { fragments: { core: { update: async () => {} } } } as unknown as Viewer;

const item = (boqItemRef: string, bccId: string): MappedItem => ({
  boqItemRef, bccId, description: boqItemRef, unit: "m³", unitRate: 1, boqQuantity: 1,
});

const element = (globalId: string, refs: string[]): MappedElement => ({
  globalId, ifcClass: "IFCCOLUMN", storey: "01 - Entry Level", boqItemRefs: refs, confidence: 0.9,
});

const centre = (bccId: string, alertLevel: string, actualPct = 50): CostCentreEvm => ({
  bccId,
  discipline: "STR",
  packageCode: "EP-TEST",
  lifecycle: "IN_PROGRESS",
  alertLevel,
  bac: 1000,
  plannedPct: actualPct,
  actualPct,
  pv: 0, ev: 0, ac: 0,
  cpi: 1, spi: 1, eac: 1000, vac: 0,
  pctBudgetConsumed: 0,
});

/** Two mapped elements on one centre, plus one the bill prices nothing for. */
function fakeIndex(): ElementMapIndex {
  const items = [item("2.04", "BCC-A")];
  const resolved = (localId: number, refs: string[]): ResolvedElement => ({
    localId,
    element: element(`G${localId}`, refs),
    items: items.filter((i) => refs.includes(i.boqItemRef)),
  });

  const byLocalId = new Map<number, ResolvedElement>([
    [1, resolved(1, ["2.04"])],
    [2, resolved(2, ["2.04"])],
    [3, resolved(3, [])],
  ]);

  return {
    byLocalId,
    itemByRef: new Map(items.map((i) => [i.boqItemRef, i])),
    mappedLocalIds: [1, 2],
    unmappedLocalIds: [3],
    confidenceBands: [],
    map: { items, elements: [], rules: [], unmapped: [] } as unknown as ElementMap,
    notInModel: 0,
  };
}

/** Every highlight material the run passed to the model. */
const materials = (m: ReturnType<typeof fakeModel>) =>
  m.calls.filter((c) => c.fn === "highlight").map((c) => c.arg as FRAGS.MaterialDefinition);

describe("ifcPaint applies colour in a form the next paint can overwrite", () => {
  it("never reaches for setColor or setOpacity", async () => {
    const model = fakeModel();
    const index = fakeIndex();

    await paintIfcByCostCentre(viewer, asModel(model), index, [centre("BCC-A", "GREEN")]);

    const sequence: BuildSequence = new Map([["BCC-A", [1, 2]]]);
    const frame = frameAt(1, sequence, new Map([[1, [centre("BCC-A", "GREEN", 100)]]]));
    await paintSequenceFrame(viewer, asModel(model), index, frame, null);

    expect(model.setColor).not.toHaveBeenCalled();
    expect(model.setOpacity).not.toHaveBeenCalled();
  });

  it("spells out every property on each highlight, so none is inherited from the last one", async () => {
    const model = fakeModel();
    await paintIfcByCostCentre(viewer, asModel(model), fakeIndex(), [centre("BCC-A", "AMBER")]);

    const applied = materials(model);
    expect(applied.length).toBeGreaterThan(0);
    for (const m of applied) {
      expect(m.color).toBeDefined();
      expect(m.opacity).toBeDefined();
      expect(m.transparent).toBe((m.opacity ?? 1) < 1);
      expect(m.renderedFaces).toBe(FRAGS.RenderedFaces.TWO);
      // The flag that made the old paint stick. Set by setColor/setOpacity, never by us.
      expect((m as unknown as { _explicitProps?: string[] })._explicitProps).toBeUndefined();
    }
  });

  it("clears whatever painted before it on the first frame of a run", async () => {
    const model = fakeModel();
    const index = fakeIndex();
    const sequence: BuildSequence = new Map([["BCC-A", [1, 2]]]);
    const centres = new Map([[1, [centre("BCC-A", "GREEN", 100)]]]);

    const first = frameAt(1, sequence, centres);
    await paintSequenceFrame(viewer, asModel(model), index, first, null);

    // Before any colour lands, so the sequence owns the model rather than inheriting a stale paint.
    const reset = model.calls.findIndex((c) => c.fn === "resetHighlight");
    const paint = model.calls.findIndex((c) => c.fn === "highlight");
    expect(reset).toBeGreaterThanOrEqual(0);
    expect(reset).toBeLessThan(paint);

    // And only on the first frame — a per-frame reset would undo the diff the painter depends on.
    model.calls.length = 0;
    const second = frameAt(1, sequence, centres);
    await paintSequenceFrame(viewer, asModel(model), index, second, first);
    expect(model.calls.some((c) => c.fn === "resetHighlight")).toBe(false);
  });

  it("recolours an element whose centre changed alert between frames", async () => {
    const model = fakeModel();
    const index = fakeIndex();
    const sequence: BuildSequence = new Map([["BCC-A", [1, 2]]]);

    const green = frameAt(1, sequence, new Map([[1, [centre("BCC-A", "GREEN", 100)]]]));
    await paintSequenceFrame(viewer, asModel(model), index, green, null);
    const greenColor = materials(model).find((m) => m.color)?.color?.getHex();

    model.calls.length = 0;
    const amber = frameAt(1, sequence, new Map([[1, [centre("BCC-A", "AMBER", 100)]]]));
    await paintSequenceFrame(viewer, asModel(model), index, amber, green);

    const repainted = materials(model).map((m) => m.color?.getHex());
    expect(repainted.length).toBeGreaterThan(0);
    expect(repainted).not.toContain(greenColor);
  });
});
