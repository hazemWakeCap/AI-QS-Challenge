import { describe, expect, it } from "vitest";
import type { CostCentreEvm } from "../api/client";
import { frameReadout, totalsOf } from "./buildVideoStats";

/**
 * The overlay's figures have to survive being read off a paused frame by someone holding the EVM
 * tab open beside it. Two things can break that: the totals drifting from the API's own sum, and
 * the rising list saying something the picture cannot show.
 */

const centre = (over: Partial<CostCentreEvm> & { bccId: string }): CostCentreEvm => ({
  discipline: "Structural Works",
  packageCode: "EP-STR-CON",
  lifecycle: "IN_PROGRESS",
  alertLevel: "GREEN",
  bac: 1_000_000,
  plannedPct: 0,
  actualPct: 0,
  pv: 0, ev: 0, ac: 0,
  cpi: null, spi: null,
  eac: 0, vac: 0,
  pctBudgetConsumed: null,
  ...over,
});

describe("period totals", () => {
  it("sums the rows the way the API does", () => {
    // Mirrors DashboardController.Totals: plain sums, CPI-method EAC, CV as EV − AC.
    const t = totalsOf(7, [
      centre({ bccId: "A", bac: 100, pv: 60, ev: 50, ac: 55 }),
      centre({ bccId: "B", bac: 300, pv: 140, ev: 130, ac: 145 }),
    ]);

    expect(t.bac).toBe(400);
    expect(t.pv).toBe(200);
    expect(t.ev).toBe(180);
    expect(t.ac).toBe(200);
    expect(t.cv).toBe(-20);
    expect(t.cpi).toBeCloseTo(0.9, 10);
    expect(t.spi).toBeCloseTo(0.9, 10);
    expect(t.eac).toBeCloseTo((400 * 200) / 180, 10);
    expect(t.vac).toBeCloseTo(400 - (400 * 200) / 180, 10);
    expect(t.earnedPct).toBeCloseTo(45, 10);
    expect(t.plannedPct).toBeCloseTo(50, 10);
  });

  it("forecasts the budget rather than dividing by zero before anything is earned", () => {
    const t = totalsOf(1, [centre({ bccId: "A", bac: 500 })]);
    expect(t.eac).toBe(500);
    expect(t.vac).toBe(0);
    expect(t.cpi).toBeNull();
    expect(t.spi).toBeNull();
  });

  it("has no opinion about a period with no rows", () => {
    expect(frameReadout(99, new Map(), new Set())).toBeNull();
  });
});

/** Two on-model structure centres, one off-model fit-out centre, over two periods. */
function panel(): Map<number, CostCentreEvm[]> {
  return new Map([
    [6, [
      centre({ bccId: "BCC-STR-CON-206", actualPct: 31.1, bac: 4_000_000, ev: 1_244_000, ac: 1_300_000 }),
      centre({ bccId: "BCC-STR-RBR-214", actualPct: 17.9, bac: 8_000_000, ev: 1_432_000, ac: 1_500_000 }),
      centre({ bccId: "BCC-ARC-TILE-301", actualPct: 4, bac: 2_000_000, ev: 80_000, ac: 90_000,
        discipline: "Architectural Fini", packageCode: "EP-ARC-TILE" }),
    ]],
    [7, [
      centre({ bccId: "BCC-STR-CON-206", actualPct: 42.7, bac: 4_000_000, ev: 1_708_000, ac: 1_800_000,
        alertLevel: "AMBER" }),
      centre({ bccId: "BCC-STR-RBR-214", actualPct: 26.0, bac: 8_000_000, ev: 2_080_000, ac: 2_200_000 }),
      centre({ bccId: "BCC-ARC-TILE-301", actualPct: 9, bac: 2_000_000, ev: 180_000, ac: 200_000,
        discipline: "Architectural Fini", packageCode: "EP-ARC-TILE" }),
    ]],
  ]);
}

const onModel = new Set(["BCC-STR-CON-206", "BCC-STR-RBR-214"]);

describe("the frame readout", () => {
  it("ranks the rising list by the value of the work, not by percentage gained", () => {
    // CON-206 gained more points (11.6 vs 8.1) but RBR-214 is twice the budget, so more money went
    // into the ground on the reinforcement. A list ordered by percentage would put the smaller
    // centre first and quietly mis-state where the month went.
    const r = frameReadout(7, panel(), onModel)!;
    expect(r.rising.map((c) => c.bccId)).toEqual(["BCC-STR-RBR-214", "BCC-STR-CON-206"]);
    expect(r.rising[0].earned).toBeCloseTo(8_000_000 * 0.081, 6);
    expect(r.rising[0].deltaPp).toBeCloseTo(8.1, 10);
    expect(r.rising[1].deltaPp).toBeCloseTo(11.6, 10);
  });

  it("carries the centre's own package and alert, for the row and its dot", () => {
    const r = frameReadout(7, panel(), onModel)!;
    const con = r.rising.find((c) => c.bccId === "BCC-STR-CON-206")!;
    expect(con.packageCode).toBe("EP-STR-CON");
    expect(con.discipline).toBe("Structural Works");
    expect(con.alertLevel).toBe("AMBER");
  });

  it("names only centres the model can actually show rising", () => {
    // The fit-out centre moved 5 points this period, but no element on screen belongs to it.
    const r = frameReadout(7, panel(), onModel)!;
    expect(r.rising.map((c) => c.bccId)).not.toContain("BCC-ARC-TILE-301");
    expect(r.disciplines).toEqual(["Structural Works"]);
  });

  it("counts the movement it is not showing, so the list cannot read as the whole job", () => {
    const r = frameReadout(7, panel(), onModel)!;
    expect(r.projectMoving).toBe(3);      // including the off-model fit-out centre
    expect(r.risingMore).toBe(0);         // both on-model movers fit in the list
    expect(r.onModelBac).toBe(12_000_000);
    expect(r.totals.bac).toBe(14_000_000); // the whole bill, never just the model's share
  });

  it("holds the list to its limit and says how many it dropped", () => {
    const r = frameReadout(7, panel(), onModel, 1)!;
    expect(r.rising).toHaveLength(1);
    expect(r.risingMore).toBe(1);
  });

  it("treats a centre's whole progress as this period's gain at the first period", () => {
    // There is no period 5 in the panel, so period 6 has nothing to difference against.
    const r = frameReadout(6, panel(), onModel)!;
    expect(r.rising.map((c) => c.bccId)).toEqual(["BCC-STR-RBR-214", "BCC-STR-CON-206"]);
    expect(r.rising[0].deltaPp).toBeCloseTo(17.9, 10);
  });

  it("leaves out a centre that did not move, and one that went backwards", () => {
    const p = panel();
    p.set(7, [
      centre({ bccId: "BCC-STR-CON-206", actualPct: 31.1 }),                    // flat
      centre({ bccId: "BCC-STR-RBR-214", actualPct: 10, bac: 8_000_000 }),      // reported down
    ]);
    const r = frameReadout(7, p, onModel)!;
    expect(r.rising).toHaveLength(0);
    expect(r.projectMoving).toBe(0);
  });

  it("orders equal-value movers by id, so two renders cannot disagree", () => {
    const p = new Map([
      [7, [
        centre({ bccId: "BCC-STR-RBR-214", actualPct: 10, bac: 1_000_000 }),
        centre({ bccId: "BCC-STR-CON-206", actualPct: 10, bac: 1_000_000 }),
      ]],
    ]);
    const r = frameReadout(7, p, onModel)!;
    expect(r.rising.map((c) => c.bccId)).toEqual(["BCC-STR-CON-206", "BCC-STR-RBR-214"]);
  });
});
