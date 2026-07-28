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

let configured: OBC.IfcLoader | null = null;

async function loaderFor(viewer: Viewer): Promise<OBC.IfcLoader> {
  if (configured) return configured;

  const loader = viewer.components.get(OBC.IfcLoader);
  await loader.setup({
    autoSetWasm: false,
    wasm: { path: "/wasm/", absolute: true },
  });

  configured = loader;
  return loader;
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
