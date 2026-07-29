import * as OBC from "@thatopen/components";
import * as THREE from "three";
import * as FRAGS from "@thatopen/fragments";
import type { Viewer } from "./viewer";

/**
 * Picking an element out of a real IFC.
 *
 * The massing tab raycasts three.js meshes it generated itself and reads a `userData.zoneCode` it
 * stamped on. None of that transfers here: Fragments geometry is streamed and instanced, and the
 * only handle on an element is the `localId` in the model's own index.
 *
 * <b>Why `OBC.Raycasters` and not `model.raycast`.</b> `FragmentsModel.raycast` exists and is
 * typed, but it is not what picks in this version — `SimpleRaycaster.castRay` routes through
 * `FastModelPickers.getFullPick`, a GPU read-back, and that is the path that actually resolves a
 * streamed instance to an id. Calling `model.raycast` directly returns null against a loaded IFC,
 * which is a silent miss rather than an error, so it looks exactly like clicking empty space.
 *
 * Position is in normalised device coordinates, matching `OBC.Mouse.position`
 * (`(clientX - left) / width * 2 - 1`) — the same formula the massing tab already uses.
 */

/** The blue the massing tab selects with, so selection reads identically in both 3D views. */
const SELECTION_COLOR = 0x2f6fe0;

const SELECTION_STYLE: FRAGS.MaterialDefinition = {
  color: new THREE.Color(SELECTION_COLOR),
  renderedFaces: FRAGS.RenderedFaces.TWO,
  opacity: 1,
  transparent: false,
};

export async function elementAtPointer(
  viewer: Viewer,
  event: { clientX: number; clientY: number },
  canvas: HTMLCanvasElement,
): Promise<number | null> {
  const rect = canvas.getBoundingClientRect();
  const position = new THREE.Vector2(
    ((event.clientX - rect.left) / rect.width) * 2 - 1,
    -((event.clientY - rect.top) / rect.height) * 2 + 1,
  );

  const caster = viewer.components.get(OBC.Raycasters).get(viewer.world);
  const hit = await caster.castRay({ position });

  // `castRay` is typed as returning a THREE.Intersection, but on the Fragments path it returns the
  // picker's own result, which carries the localId. The typing is behind the implementation.
  return (hit as unknown as { localId?: number } | null)?.localId ?? null;
}

/**
 * Marks the selected element.
 *
 * Uses `highlight` rather than `setColor` on purpose: the cost paint owns colour, and overwriting it
 * to show selection would destroy the very reading the user clicked to investigate. A highlight sits
 * on top and lifts off cleanly.
 */
export async function showSelection(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  localId: number | null,
  previous: number | null,
): Promise<void> {
  if (previous !== null && previous !== localId) model.resetHighlight([previous]);
  if (localId !== null) model.highlight([localId], SELECTION_STYLE);
  await viewer.fragments.core.update(true);
}
