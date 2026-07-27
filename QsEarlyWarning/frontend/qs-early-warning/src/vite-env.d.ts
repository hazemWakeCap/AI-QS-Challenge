/// <reference types="vite/client" />

// Vite's `?url` suffix resolves an asset to its emitted URL. vite/client's generic `*?url`
// declaration does not match a bare package subpath, so the fragments worker is declared
// explicitly. It is imported this way — rather than via FragmentsManager.getWorker(), which
// fetches a version-matched copy from unpkg at runtime — so the viewer needs no network.
declare module "@thatopen/fragments/worker?url" {
  const src: string;
  export default src;
}
