import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
  budgetCells, modelScopeLabel, monthLabel, periodLabel, projectHeading, risingDeltaLabel,
  risingHeading, risingMoreLabel, risingPctLabel, standingLabel, unpricedLabel,
} from "./buildVideoCopy";
import type { FrameReadout } from "./buildVideoStats";

/**
 * The stage is drawn twice by two different mechanisms — as DOM for the headless renderer, and into
 * a 2D canvas for the in-browser recorder. Two copies of the same sentence is two copies that can
 * drift, and the one that would drift unnoticed is the caption: the line that stops the sequence
 * being read as a claim the data does not support.
 *
 * So this asserts the obvious-but-load-bearing thing — that neither surface hard-codes wording of
 * its own. It reads the sources, because a runtime test would pass happily while a literal sat
 * inside a component nobody re-rendered.
 */

const read = (p: string) => readFileSync(new URL(p, import.meta.url), "utf-8");

const SURFACES = [
  ["the DOM overlay", "../components/RenderOverlay.tsx"],
  ["the canvas compositor", "./videoCompositor.ts"],
] as const;

/** The two hosts that mount the DOM overlay. Neither may grow an overlay of its own. */
const HOSTS = [
  ["the render harness", "../components/RenderHarness.tsx"],
  ["the in-app panel", "../components/BuildVideo.tsx"],
] as const;

const readout = (over: Partial<FrameReadout> = {}): FrameReadout => ({
  period: 7,
  rising: [],
  risingMore: 0,
  projectMoving: 99,
  disciplines: ["Structural Works"],
  onModelBac: 27_200_000,
  totals: {
    period: 7,
    bac: 224_300_000, pv: 43_000_000, ev: 31_300_000, ac: 32_900_000,
    cv: -1_600_000, cpi: 0.951, spi: 0.728,
    eac: 235_800_000, vac: -11_500_000,
    earnedPct: 13.95, plannedPct: 19.17,
  },
  ...over,
});

describe("build-video wording", () => {
  it("is imported by every surface that draws it, never re-typed", () => {
    for (const [name, path] of SURFACES) {
      const src = read(path);
      expect(src, `${name} should import the shared copy`).toMatch(/from "\.\.?\/(model\/)?buildVideoCopy"/);
    }
  });

  it("has no surface hard-coding the caption", () => {
    // The exact failure this guards: someone edits the caption in one place and the video keeps
    // saying the old thing, or vice versa.
    for (const [name, path] of SURFACES) {
      const src = read(path);
      expect(src, `${name} must not inline the caption lead`).not.toContain(VIDEO_CAPTION_LEAD);
      expect(src, `${name} must not inline the caption body`).not.toContain(VIDEO_CAPTION_BODY);
    }
  });

  it("has both hosts mount the one overlay rather than each keeping a copy", () => {
    // The failure this replaces: the harness and the in-app panel each carried their own copy of
    // the overlay JSX, and the in-app one had already drifted — it re-typed the counts the harness
    // imported from here.
    for (const [name, path] of HOSTS) {
      const src = read(path);
      expect(src, `${name} should mount RenderOverlay`).toContain("<RenderOverlay");
      expect(src, `${name} must not draw the overlay itself`).not.toContain('className="render-overlay"');
    }
  });

  it("still says the thing it exists to say", () => {
    // Pinned deliberately: this sentence is the honesty mechanism, so a silent softening of it
    // should fail a test rather than ship.
    expect(VIDEO_CAPTION_LEAD).toBe("The order is assumed, the amounts are not.");
    expect(VIDEO_CAPTION_BODY).toContain("per cost centre, never per element");
  });

  it("formats the readout the way both surfaces expect", () => {
    expect(periodLabel(7)).toBe("Period 7");
    expect(standingLabel(806, 1127)).toBe("806 of 1,127 priced elements standing");
    expect(unpricedLabel(399)).toContain("399 elements the bill never priced");
  });

  it("keeps the title and subtitle non-empty", () => {
    expect(VIDEO_TITLE.length).toBeGreaterThan(0);
    expect(VIDEO_SUBTITLE.length).toBeGreaterThan(0);
  });
});

describe("the calendar month", () => {
  it("reads the month out of the string rather than through a Date", () => {
    // A Date would shift a bare local midnight back a month for anyone east of Greenwich, and the
    // rendered frames have to be identical wherever they are rendered.
    expect(monthLabel("2025-10-01T00:00:00")).toBe("Oct 2025");
    expect(monthLabel("2026-09-01")).toBe("Sep 2026");
  });

  it("costs the overlay a line rather than the frame when there is no date", () => {
    expect(monthLabel(null)).toBeNull();
    expect(monthLabel("")).toBeNull();
    expect(monthLabel("not a date")).toBeNull();
  });
});

describe("the rising readout", () => {
  it("names the disciplines the model can show", () => {
    expect(risingHeading(["Structural Works"])).toBe("Rising now · Structural Works");
    expect(risingHeading([])).toBe("Rising now");
  });

  it("does not let a long discipline list run off the frame", () => {
    expect(risingHeading(["Structural Works", "Civil / Earthworks", "Facade Systems", "Lifts"]))
      .toBe("Rising now · Structural Works · Civil / Earthworks +2 more");
  });

  it("rounds progress but keeps a decimal on the gain", () => {
    // A month's movement is often under a point; rounding it would print "+0pp" beside geometry
    // that visibly moved.
    expect(risingPctLabel(42.7)).toBe("43%");
    expect(risingDeltaLabel(0.4)).toBe("+0.4pp");
    expect(risingDeltaLabel(11.6)).toBe("+11.6pp");
  });

  it("always states the wider scope, listed or not", () => {
    expect(risingMoreLabel(6, 99)).toBe("+6 more on the model · 99 cost centres moving across the project");
    expect(risingMoreLabel(0, 99)).toBe("99 cost centres moving across the project");
  });

  it("says what share of the bill is actually on screen", () => {
    expect(modelScopeLabel(27_200_000, 224_300_000))
      .toBe("The centres on screen carry 27.2M of the 224.3M AED bill — 12% of it");
  });
});

describe("the budget strip", () => {
  it("states the position the EVM tab would state", () => {
    const cells = budgetCells(readout());
    expect(cells.map((c) => c.label)).toEqual([
      "Earned value", "Actual cost", "Forecast at completion", "Schedule",
    ]);
    expect(cells[0].value).toBe("31.3M of 224.3M AED");
    expect(cells[0].note).toBe("14% of the bill earned");
    expect(cells[1].value).toBe("32.9M AED · CPI 0.951");
    expect(cells[1].note).toBe("1.6M more spent than earned");
    expect(cells[2].value).toBe("235.8M AED");
    expect(cells[3].value).toBe("14% complete vs 19% planned");
  });

  it("names the direction of the overrun rather than a signed number", () => {
    // "VAC −11.5M" is a figure a QS reads correctly and everyone else reads backwards.
    expect(budgetCells(readout())[2].note).toBe("11.5M over the budget");

    const under = readout();
    under.totals = { ...under.totals, eac: 220_000_000, vac: 4_300_000, cv: 900_000 };
    expect(budgetCells(under)[2].note).toBe("4.3M under the budget");
    expect(budgetCells(under)[1].note).toBe("0.9M earned above what it cost");
  });

  it("calls the programme behind, ahead or on plan", () => {
    const at = (spi: number | null) => {
      const r = readout();
      r.totals = { ...r.totals, spi };
      return budgetCells(r)[3].note;
    };
    expect(at(0.728)).toBe("SPI 0.728 · behind plan");
    expect(at(1)).toBe("SPI 1.000 · on plan");
    expect(at(1.2)).toBe("SPI 1.200 · ahead of plan");
    expect(at(null)).toBe("SPI — · nothing planned yet");
  });

  it("heads the strip with the period it is quoting", () => {
    expect(projectHeading(7, 12)).toBe("Project to date · period 7 of 12");
  });
});
