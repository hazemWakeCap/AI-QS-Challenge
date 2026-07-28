// Copies web-ifc's wasm next to the app so the IFC loader never reaches the network.
//
// web-ifc resolves its wasm by URL at runtime. The documented default pulls it from unpkg, which
// makes opening a model depend on the venue's wifi — the same reason the fragments worker is
// bundled rather than fetched. Copying on postinstall keeps the binary version-matched to the
// installed package instead of drifting as a committed file would.
import { copyFileSync, mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join } from "node:path";

const require = createRequire(import.meta.url);
const webIfcDir = dirname(require.resolve("web-ifc"));
const target = join(process.cwd(), "public", "wasm");

mkdirSync(target, { recursive: true });

for (const file of ["web-ifc.wasm", "web-ifc-mt.wasm"]) {
  try {
    copyFileSync(join(webIfcDir, file), join(target, file));
    console.log(`copied ${file} → public/wasm/`);
  } catch (err) {
    // web-ifc-mt is optional; only the single-threaded build is required to load a model.
    if (file === "web-ifc.wasm") throw err;
    console.warn(`skipped ${file}: ${err.message}`);
  }
}
