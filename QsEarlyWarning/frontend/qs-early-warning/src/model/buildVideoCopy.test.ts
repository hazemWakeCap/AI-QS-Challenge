import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import {
  VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
  periodLabel, standingLabel, unpricedLabel,
} from "./buildVideoCopy";

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
  ["the DOM overlay", "../components/RenderHarness.tsx"],
  ["the in-app panel", "../components/BuildVideo.tsx"],
  ["the canvas compositor", "./videoCompositor.ts"],
] as const;

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
