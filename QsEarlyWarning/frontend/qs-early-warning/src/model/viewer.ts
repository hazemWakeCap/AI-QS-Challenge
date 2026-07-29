import * as OBC from "@thatopen/components";
import type * as FRAGS from "@thatopen/fragments";
import * as THREE from "three";

// The fragments worker ships inside the package. Bundling it through Vite (?url) instead of
// FragmentsManager.getWorker(), which fetches a version-matched copy from unpkg at runtime:
// the demo has to survive a conference network, and a CDN round-trip on first paint is the
// kind of dependency that fails on the day it matters.
import fragmentsWorkerUrl from "@thatopen/fragments/worker?url";

export interface Viewer {
  components: OBC.Components;
  world: OBC.SimpleWorld<OBC.SimpleScene, OBC.OrthoPerspectiveCamera, OBC.SimpleRenderer>;
  fragments: OBC.FragmentsManager;
  dispose: () => void;
}

/**
 * Boots a That Open world into a container element.
 *
 * Everything here is the documented setup path: Components → Worlds → scene/renderer/camera →
 * init → FragmentsManager. The one deviation is the locally bundled worker above.
 */
export async function createViewer(container: HTMLElement): Promise<Viewer> {
  const components = new OBC.Components();

  const worlds = components.get(OBC.Worlds);
  const world = worlds.create<OBC.SimpleScene, OBC.OrthoPerspectiveCamera, OBC.SimpleRenderer>();

  world.scene = new OBC.SimpleScene(components);
  world.renderer = new OBC.SimpleRenderer(components, container);
  world.camera = new OBC.OrthoPerspectiveCamera(components);

  components.init();

  world.scene.setup();
  world.scene.three.background = null;

  const fragments = components.get(OBC.FragmentsManager);
  fragments.init(fragmentsWorkerUrl);

  // Fragments throttles `update()` to one call per `maxUpdateRate` ms (default 100) and silently
  // drops the rest. Animating the model means asking for updates far faster than that, so the
  // throttle turns most frames into no-ops. It is read live on every call, so clearing it here is
  // enough; pacing is then whatever the caller asks for.
  fragments.core.settings.maxUpdateRate = 0;

  // Every model that lands in the manager gets wired to this world's camera + scene.
  fragments.list.onItemSet.add(({ value: model }) => {
    model.useCamera(world.camera.three);
    world.scene.three.add(model.object);
    void fragments.core.update(true);
  });

  // Re-render on camera rest — OrthoPerspectiveCamera streams geometry on demand.
  world.camera.controls?.addEventListener("rest", () => void fragments.core.update(true));

  return {
    components,
    world,
    fragments,
    dispose: () => {
      try {
        components.dispose();
      } catch {
        /* disposing a half-initialised world is not worth surfacing */
      }
    },
  };
}

/**
 * Waits until the model has actually finished redrawing, rather than guessing.
 *
 * <b>Why this is not just `await update()`.</b> Fragments processes a model as a progressive sweep
 * on a worker, and every visibility or colour change restarts that sweep from the beginning. So
 * `update(false)` returns long before the pixels are right, and `update(true)` waits for a sweep
 * that the next mutation is about to invalidate anyway. `onViewUpdated` is the library's own signal
 * that a view cycle completed, which is the only honest answer to "is this frame done".
 *
 * The timeout is a safety valve, not a pacing mechanism: a model with nothing left to do may never
 * fire the event at all, and an exporter must not hang on a frame that was already correct.
 */
export async function settle(
  viewer: Viewer,
  model: FRAGS.FragmentsModel,
  timeoutMs = 2000,
): Promise<void> {
  await new Promise<void>((resolve) => {
    let done = false;
    const finish = () => {
      if (done) return;
      done = true;
      model.onViewUpdated.remove(finish);
      clearTimeout(timer);
      resolve();
    };
    const timer = setTimeout(finish, timeoutMs);
    model.onViewUpdated.add(finish);
    void viewer.fragments.core.update(true);
  });
}

/**
 * Frames the given extents from a three-quarter view.
 *
 * Takes an explicit box rather than reading `model.getBoxes()`, which returns nothing for
 * procedurally created elements — the generator supplies the extents it drew.
 */
export async function fitToBounds(viewer: Viewer, merged: THREE.Box3): Promise<void> {
  if (merged.isEmpty()) return;

  const centre = merged.getCenter(new THREE.Vector3());
  const size = merged.getSize(new THREE.Vector3());
  const reach = Math.max(size.x, size.y, size.z);

  const controls = viewer.world.camera.controls;
  if (!controls) return;

  // setLookAt only — fitToBox afterwards would re-derive its own direction and undo this, putting
  // the camera back near ground level where a 22 m tall, 60 m wide building reads as a flat plate.
  const dir = new THREE.Vector3(0.85, 0.55, 1).normalize().multiplyScalar(reach * 1.35);

  await controls.setLookAt(
    centre.x + dir.x,
    centre.y + dir.y,
    centre.z + dir.z,
    centre.x,
    centre.y,
    centre.z,
    false,
  );
}
