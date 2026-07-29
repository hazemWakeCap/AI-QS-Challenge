import * as THREE from "three";

/**
 * A camera move that is a pure function of time.
 *
 * <b>Why not the camera's own transitions.</b> `camera-controls` animates with damping against the
 * wall clock — `smoothTime`, `rest` events, easing that depends on when frames happened to land.
 * That is right for a person dragging a mouse and wrong for a renderer, where frame 47 must look
 * identical on every run regardless of how long frame 46 took. So the pose is computed here and
 * applied with the transition disabled; the controls become a way to set a matrix, not an animator.
 *
 * The move itself is a slow azimuthal orbit at a fixed elevation — enough parallax to read the
 * building as a solid, never enough to make the viewer work out where they are.
 */

export interface CameraPose {
  position: THREE.Vector3;
  target: THREE.Vector3;
}

/** Total rotation across a full run. A little over a quarter turn reads as movement, not a carousel. */
const SWEEP_RADIANS = Math.PI * 0.55;

/** Where the orbit starts, measured so the opening frame matches the static three-quarter view. */
const START_AZIMUTH = Math.atan2(1, 0.85);

/**
 * Distance from the centre, as a multiple of the model's bounding-sphere radius.
 *
 * Derived rather than guessed: at a 60° vertical field of view, a sphere exactly fills the frame at
 * `radius / sin(30°)` = 2r, so anything below that fills more of it. 1.5 leaves the building
 * comfortably inside 16:9 with room for the overlay, without stranding it in empty space — sizing
 * off the largest single dimension instead pushed a long, flat building much too far away.
 */
const REACH = 1.5;

/** Eye height above the model centre, also in radii. Enough to read the floors as stacked. */
const ELEVATION = 0.55;

/**
 * The camera pose at `t` in 0..1.
 *
 * Starts on the same three-quarter bearing as `fitToBounds`, so a render opens on the view a user
 * would already have been looking at, then rotates from there.
 */
export function poseAt(box: THREE.Box3, t: number): CameraPose {
  const target = box.getCenter(new THREE.Vector3());
  // Bounding sphere, not the longest edge: a 60m × 20m × 12m slab and a 60m cube need very
  // different camera distances, and only the diagonal knows the difference.
  const radius = box.getSize(new THREE.Vector3()).length() / 2;

  const clamped = Math.max(0, Math.min(1, t));
  const azimuth = START_AZIMUTH + SWEEP_RADIANS * ease(clamped);

  const distance = radius * REACH;
  const position = new THREE.Vector3(
    target.x + Math.sin(azimuth) * distance,
    target.y + radius * ELEVATION,
    target.z + Math.cos(azimuth) * distance,
  );

  return { position, target };
}

/**
 * Smoothstep, so the orbit starts and ends at rest.
 *
 * A constant-rate rotation that stops dead on the last frame reads as a dropped clip; easing both
 * ends makes the move feel deliberate and lets the final frame hold while the viewer reads it.
 */
function ease(t: number): number {
  return t * t * (3 - 2 * t);
}

/**
 * The `t` values for a run of `frames` frames, inclusive of both ends.
 *
 * Kept next to the camera because the sequence clock and the camera clock must agree on what "the
 * last frame" means — an off-by-one here shows up as a video that stops just short of complete.
 */
export function timeline(frames: number, min: number, max: number): number[] {
  if (frames <= 1) return [max];
  const span = max - min;
  return Array.from({ length: frames }, (_, i) => min + (span * i) / (frames - 1));
}
