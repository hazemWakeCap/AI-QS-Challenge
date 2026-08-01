import * as OBC from "@thatopen/components";
import type * as FRAGS from "@thatopen/fragments";
import type { Viewer } from "./viewer";

/**
 * Loads a real IFC into the That Open world.
 *
 * The wasm is served from `/wasm/` (copied out of node_modules by scripts/copy-wasm.mjs) rather
 * than the documented unpkg default, so opening a model never depends on the venue's network.
 */

const MODEL_ID = "ifc-takeoff-model";

/**
 * Which worlds have already had their loader configured.
 *
 * Keyed by the world, not global. `setup()` is slow enough to be worth doing once, but the loader
 * it configures belongs to one `Components` — and reads its `FragmentsManager` off that same
 * instance at load time. Caching one loader across worlds meant that once a tab unmounted and
 * disposed its world, every later tab got a loader pointing at the dead one, whose fragments core
 * is gone: "You need to initialize fragments first". A WeakMap keeps the once-only setup while
 * letting each live world have its own loader, and forgets a world as soon as it is collected.
 */
const configured = new WeakMap<OBC.Components, Promise<OBC.IfcLoader>>();

function loaderFor(viewer: Viewer): Promise<OBC.IfcLoader> {
  const existing = configured.get(viewer.components);
  if (existing) return existing;

  const setup = (async () => {
    const loader = viewer.components.get(OBC.IfcLoader);
    await loader.setup({
      autoSetWasm: false,
      wasm: { path: "/wasm/", absolute: true },
    });
    return loader;
  })();

  // Stored before it settles, so two loads racing on the same world share one setup. A failed
  // setup is dropped rather than cached, so a retry is not stuck with the failure.
  configured.set(viewer.components, setup);
  setup.catch(() => configured.delete(viewer.components));
  return setup;
}

/** Drops any previously loaded model so a second file cannot show the first one's geometry. */
export async function disposeIfc(viewer: Viewer): Promise<void> {
  if (viewer.fragments.list.has(MODEL_ID)) {
    await viewer.fragments.core.disposeModel(MODEL_ID);
  }
}

/**
 * Loads IFC bytes and returns the resulting model.
 *
 * Unlike the generated massing, a real IFC arrives with its own item index, so everything the
 * generated path could not do — `getLocalIds`, `getItemsData`, `getBoxes`, per-item colour — works
 * here. That index is the whole reason this tab uses Fragments and the other one does not.
 */
export async function loadIfc(
  viewer: Viewer,
  bytes: Uint8Array,
  onProgress?: (fraction: number) => void,
): Promise<FRAGS.FragmentsModel> {
  const loader = await loaderFor(viewer);
  await disposeIfc(viewer);

  const model = await loader.load(bytes, false, MODEL_ID, {
    processData: {
      progressCallback: (progress: number) => onProgress?.(progress),
    },
  });

  await viewer.fragments.core.update(true);
  return model;
}

/** Fetches the bundled sample model. */
export async function fetchBundledIfc(url = "/models/school_str.ifc"): Promise<Uint8Array> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`Could not load ${url} (${res.status}).`);
  return new Uint8Array(await res.arrayBuffer());
}

/** Reads a user-picked file. */
export async function readIfcFile(file: File): Promise<Uint8Array> {
  return new Uint8Array(await file.arrayBuffer());
}
