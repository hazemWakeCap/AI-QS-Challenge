import * as THREE from "three";
import { describe, expect, it } from "vitest";
import { poseAt, timeline } from "./cameraPath";

/**
 * The camera is the one part of a rendered frame that could drift without anyone noticing — a
 * slightly different pose still looks like a building. These tests pin the properties a renderer
 * depends on: same input, same pose; and a timeline that actually reaches both ends.
 */

/** Roughly the bundled school: long, shallow, low. The shape that broke a naive framing rule. */
const BOX = new THREE.Box3(
  new THREE.Vector3(-30, 0, -10),
  new THREE.Vector3(30, 12, 10),
);

describe("poseAt", () => {
  it("is a pure function of t", () => {
    const a = poseAt(BOX, 0.37);
    const b = poseAt(BOX, 0.37);
    expect(a.position.toArray()).toEqual(b.position.toArray());
    expect(a.target.toArray()).toEqual(b.target.toArray());
  });

  it("always looks at the centre of the model", () => {
    const centre = BOX.getCenter(new THREE.Vector3());
    for (const t of [0, 0.25, 0.5, 0.75, 1]) {
      expect(poseAt(BOX, t).target.toArray()).toEqual(centre.toArray());
    }
  });

  it("holds a constant distance and height, so the model never appears to breathe", () => {
    const centre = BOX.getCenter(new THREE.Vector3());
    const radii = [0, 0.3, 0.6, 1].map((t) => {
      const p = poseAt(BOX, t).position;
      return Math.hypot(p.x - centre.x, p.z - centre.z);
    });
    for (const r of radii) expect(r).toBeCloseTo(radii[0], 6);

    const heights = [0, 0.5, 1].map((t) => poseAt(BOX, t).position.y);
    for (const h of heights) expect(h).toBeCloseTo(heights[0], 6);
  });

  it("frames off the diagonal, so a long flat building is not pushed away by its length", () => {
    // Sizing off the longest edge put a 60m-long, 12m-tall slab far enough away to be a speck.
    // The distance must track the bounding sphere, which barely grows when only one edge does.
    const longer = new THREE.Box3(new THREE.Vector3(-60, 0, -10), new THREE.Vector3(60, 12, 10));
    const centre = BOX.getCenter(new THREE.Vector3());
    const near = poseAt(BOX, 0).position.distanceTo(centre);
    const far = poseAt(longer, 0).position.distanceTo(longer.getCenter(new THREE.Vector3()));
    expect(far).toBeGreaterThan(near);
    expect(far / near).toBeLessThan(2.2); // grew with the diagonal, not with the doubled edge
  });

  it("rotates monotonically and starts and ends at rest", () => {
    const centre = BOX.getCenter(new THREE.Vector3());
    const azimuth = (t: number) => {
      const p = poseAt(BOX, t).position;
      return Math.atan2(p.x - centre.x, p.z - centre.z);
    };
    const samples = Array.from({ length: 21 }, (_, i) => azimuth(i / 20));
    const steps = samples.slice(1).map((a, i) => a - samples[i]);

    for (const s of steps) expect(s).toBeGreaterThanOrEqual(0);
    // Eased: the first and last steps are the smallest, so the move settles instead of cutting.
    expect(steps[0]).toBeLessThan(Math.max(...steps));
    expect(steps[steps.length - 1]).toBeLessThan(Math.max(...steps));
  });

  it("clamps outside 0..1 rather than spinning past the end", () => {
    expect(poseAt(BOX, 1.8).position.toArray()).toEqual(poseAt(BOX, 1).position.toArray());
    expect(poseAt(BOX, -0.4).position.toArray()).toEqual(poseAt(BOX, 0).position.toArray());
  });
});

describe("timeline", () => {
  it("spans the full period range inclusive of both ends", () => {
    const t = timeline(240, 1, 12);
    expect(t).toHaveLength(240);
    expect(t[0]).toBe(1);
    expect(t[t.length - 1]).toBe(12);
  });

  it("is evenly spaced", () => {
    const t = timeline(5, 1, 12);
    expect(t).toEqual([1, 3.75, 6.5, 9.25, 12]);
  });

  it("renders the finished building when asked for a single frame", () => {
    // Not the first period — a one-frame render is a poster, and a poster of an empty site is useless.
    expect(timeline(1, 1, 12)).toEqual([12]);
  });
});
