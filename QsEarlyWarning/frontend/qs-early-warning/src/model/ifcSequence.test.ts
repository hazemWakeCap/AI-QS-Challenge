import { describe, expect, it } from "vitest";
import type { CostCentreEvm, ProgressForecast, ProgressPoint } from "../api/client";
import type { BuildSequence } from "./ifcSequence";
import { buildProgressIndex, frameAt, statesAt, tierAt } from "./ifcSequence";

/**
 * The sequence maths, including the projection past the last reported period.
 *
 * Two properties matter most here and are asserted hardest. First, that passing no projection leaves
 * the measured behaviour byte-for-byte as it was — the video renderer shares this module and its
 * output is checksum-compared. Second, that a projected element is always distinguishable from a
 * built one, because that distinction is the entire licence for drawing a forecast on a model.
 */

const centre = (bccId: string, actualPct: number, alertLevel = "GREEN"): CostCentreEvm => ({
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

/** A ten-element centre, so one element is exactly 10% of the sequence. */
const sequenceOf = (bccId: string, n = 10): BuildSequence =>
  new Map([[bccId, Array.from({ length: n }, (_, i) => i + 1)]]);

const point = (period: number, p50: number, p10: number, p90: number, tier: ProgressPoint["tier"]): ProgressPoint =>
  ({ period, p50Pct: p50, p10Pct: p10, p90Pct: p90, tier });

const forecastOf = (bccId: string, points: ProgressPoint[], opts?: Partial<ProgressForecast>): ProgressForecast => ({
  originPeriod: 12,
  horizonPeriod: 15,
  backtestedThroughPeriod: 15,
  suggestedHorizonPeriod: 15,
  method: "recent 3-period progress pace",
  centres: [{
    bccId,
    originPeriod: 12,
    actualPctAtOrigin: 50,
    pacePctPerPeriod: 10,
    projectedFinishPeriod: 17,
    stalled: false,
    alertAtOrigin: "AMBER",
    points,
  }],
  validation: {
    provenance: "test", originMin: 4, originMax: 11, centres: 1,
    metrics: [], bands: [], notes: [],
  },
  ...opts,
});

describe("measured periods, with no projection", () => {
  const byPeriod = new Map<number, CostCentreEvm[]>([
    [1, [centre("BCC-A", 0)]],
    [2, [centre("BCC-A", 50)]],
    [3, [centre("BCC-A", 100)]],
  ]);

  it("reveals elements in proportion to reported progress", () => {
    const seq = sequenceOf("BCC-A");
    expect(frameAt(1, seq, byPeriod).builtCount).toBe(0);
    expect(frameAt(2, seq, byPeriod).builtCount).toBe(5);
    expect(frameAt(3, seq, byPeriod).builtCount).toBe(10);
  });

  it("interpolates between periods so the building rises continuously", () => {
    const s = statesAt(1.5, byPeriod).get("BCC-A")!;
    expect(s.progress).toBeCloseTo(0.25, 6);
    expect(s.tier).toBe("measured");
  });

  it("carries no band, and therefore no shell", () => {
    const s = statesAt(2, byPeriod).get("BCC-A")!;
    expect(s.progressLow).toBe(s.progress);
    expect(s.progressHigh).toBe(s.progress);

    const f = frameAt(2, sequenceOf("BCC-A"), byPeriod);
    expect(f.shell.size).toBe(0);
    expect(f.shellCount).toBe(0);
    expect([...f.confident]).toEqual([...f.built]);
    expect(f.tier).toBe("measured");
  });

  it("takes the alert from the nearer period rather than blending it", () => {
    const alerts = new Map<number, CostCentreEvm[]>([
      [1, [centre("BCC-A", 20, "GREEN")]],
      [2, [centre("BCC-A", 40, "AMBER")]],
    ]);
    expect(statesAt(1.4, alerts).get("BCC-A")!.alertLevel).toBe("GREEN");
    expect(statesAt(1.6, alerts).get("BCC-A")!.alertLevel).toBe("AMBER");
  });
});

describe("projected periods", () => {
  // Origin 12, back-tested through 15 (+3), horizon 16 — so period 16 is the first extrapolated one.
  const byPeriod = new Map<number, CostCentreEvm[]>([[12, [centre("BCC-A", 50, "AMBER")]]]);
  const progress = buildProgressIndex(forecastOf("BCC-A", [
    point(12, 50, 50, 50, "Measured"),
    point(13, 60, 55, 80, "Forecast"),
    point(14, 70, 60, 95, "Forecast"),
    point(15, 80, 65, 100, "Forecast"),
    point(16, 90, 70, 100, "Extrapolated"),
  ], { horizonPeriod: 16 }));

  it("splits the band into a solid core and a translucent shell", () => {
    // P13: P10 55% → 5 confident, P50 60% → 6 built, P90 80% → 8 reached.
    const f = frameAt(13, sequenceOf("BCC-A"), byPeriod, progress);
    expect(f.confident.size).toBe(5);
    expect(f.built.size).toBe(6);
    expect(f.shell.size).toBe(2);
    expect(f.tier).toBe("forecast");
  });

  it("keeps the shell disjoint from what is built", () => {
    const f = frameAt(14, sequenceOf("BCC-A"), byPeriod, progress);
    for (const id of f.built) expect(f.shell.has(id)).toBe(false);
    // Confident is a subset of built — the pessimistic case cannot exceed the median.
    for (const id of f.confident) expect(f.built.has(id)).toBe(true);
  });

  it("widens the shell as the horizon lengthens", () => {
    const a = frameAt(13, sequenceOf("BCC-A"), byPeriod, progress);
    const b = frameAt(15, sequenceOf("BCC-A"), byPeriod, progress);
    expect(b.shell.size + b.built.size).toBeGreaterThan(a.shell.size + a.built.size);
  });

  it("marks the extrapolated tier past the back-tested horizon", () => {
    // The tier is derived from the projection's boundaries, not read off each point — one rule, so a
    // point's own label can never disagree with the period it sits in.
    expect(frameAt(15, sequenceOf("BCC-A"), byPeriod, progress).tier).toBe("forecast");
    expect(frameAt(16, sequenceOf("BCC-A"), byPeriod, progress).tier).toBe("extrapolated");
    expect(tierAt(12, progress)).toBe("measured");
    expect(tierAt(15, progress)).toBe("forecast");
    expect(tierAt(16, progress)).toBe("extrapolated");
  });

  it("carries the origin alert forward into projected periods", () => {
    expect(statesAt(14, byPeriod, progress).get("BCC-A")!.alertLevel).toBe("AMBER");
  });

  it("still reads the measured alert on a frame straddling the origin", () => {
    // At t=12.4 the nearer period is 12, which the workbook reports — so the measured verdict stands.
    expect(statesAt(12.4, byPeriod, progress).get("BCC-A")!.alertLevel).toBe("AMBER");
  });

  it("interpolates the band endpoints, not just the median", () => {
    const s = statesAt(13.5, byPeriod, progress).get("BCC-A")!;
    expect(s.progress).toBeCloseTo(0.65, 6);
    expect(s.progressLow).toBeCloseTo(0.575, 6);
    expect(s.progressHigh).toBeCloseTo(0.875, 6);
  });
});

describe("certainty wins over doubt across trades", () => {
  it("an element another trade has firmly built is not drawn as uncertain", () => {
    // Two centres over the same ten elements: one has firmly built all ten, the other is mid-band.
    const seq: BuildSequence = new Map([
      ["BCC-SURE", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]],
      ["BCC-UNSURE", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]],
    ]);
    const byPeriod = new Map<number, CostCentreEvm[]>([
      [12, [centre("BCC-SURE", 100), centre("BCC-UNSURE", 20)]],
    ]);
    const progress = buildProgressIndex({
      ...forecastOf("BCC-SURE", []),
      centres: [
        {
          bccId: "BCC-SURE", originPeriod: 12, actualPctAtOrigin: 100, pacePctPerPeriod: 0,
          projectedFinishPeriod: 12, stalled: false, alertAtOrigin: "GREEN",
          points: [point(12, 100, 100, 100, "Measured"), point(13, 100, 100, 100, "Forecast")],
        },
        {
          bccId: "BCC-UNSURE", originPeriod: 12, actualPctAtOrigin: 20, pacePctPerPeriod: 10,
          projectedFinishPeriod: 20, stalled: false, alertAtOrigin: "AMBER",
          points: [point(12, 20, 20, 20, "Measured"), point(13, 30, 25, 70, "Forecast")],
        },
      ],
    });

    const f = frameAt(13, seq, byPeriod, progress);
    expect(f.built.size).toBe(10);
    expect(f.shell.size).toBe(0);
  });

  it("colours a multi-trade element by the trade in trouble", () => {
    const seq: BuildSequence = new Map([["BCC-A", [1]], ["BCC-B", [1]]]);
    const byPeriod = new Map<number, CostCentreEvm[]>([
      [1, [centre("BCC-A", 100, "GREEN"), centre("BCC-B", 100, "AMBER")]],
    ]);
    expect(frameAt(1, seq, byPeriod).alertByLocalId.get(1)).toBe("AMBER");
  });
});

describe("determinism", () => {
  it("produces identical frames for identical inputs", () => {
    // The video renderer double-renders and diffs frame checksums; drift here would break it silently.
    const byPeriod = new Map<number, CostCentreEvm[]>([[12, [centre("BCC-A", 50)]]]);
    const progress = buildProgressIndex(forecastOf("BCC-A", [
      point(12, 50, 50, 50, "Measured"),
      point(13, 62.5, 55, 81, "Forecast"),
    ]));
    const seq = sequenceOf("BCC-A", 37);

    const a = frameAt(12.7, seq, byPeriod, progress);
    const b = frameAt(12.7, seq, byPeriod, progress);
    expect([...a.built].sort()).toEqual([...b.built].sort());
    expect([...a.shell].sort()).toEqual([...b.shell].sort());
    expect([...a.confident].sort()).toEqual([...b.confident].sort());
  });
});
