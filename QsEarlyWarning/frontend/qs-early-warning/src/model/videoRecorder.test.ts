import { describe, expect, it } from "vitest";
import { pickMimeType } from "./videoRecorder";

/**
 * The container choice is the one part of recording that varies by browser, so it is the part worth
 * pinning. Everything else in the recorder touches `MediaRecorder` and a live canvas, which vitest
 * has neither of — and stubbing them deeply enough to be meaningful would test the stub.
 */

describe("pickMimeType", () => {
  it("prefers mp4, which is what travels", () => {
    // Matches the CLI's output and opens in anything, so it wins when the browser offers it.
    const picked = pickMimeType(() => true);
    expect(picked?.mimeType).toMatch(/^video\/mp4/);
    expect(picked?.extension).toBe("mp4");
  });

  it("falls back to webm rather than refusing", () => {
    const picked = pickMimeType((t) => t.startsWith("video/webm"));
    expect(picked?.extension).toBe("webm");
    expect(picked?.mimeType).toBe("video/webm;codecs=vp9");
  });

  it("prefers vp9 over vp8 when both are offered", () => {
    const picked = pickMimeType((t) => t === "video/webm;codecs=vp9" || t === "video/webm;codecs=vp8");
    expect(picked?.mimeType).toBe("video/webm;codecs=vp9");
  });

  it("takes the bare container when no codec string is recognised", () => {
    const picked = pickMimeType((t) => t === "video/webm");
    expect(picked?.mimeType).toBe("video/webm");
  });

  it("returns null when the browser will record nothing", () => {
    // The panel disables its button on null rather than throwing halfway through a render.
    expect(pickMimeType(() => false)).toBeNull();
  });

  it("labels the format so the UI can say what it is about to produce", () => {
    expect(pickMimeType(() => true)?.label).toContain("MP4");
    expect(pickMimeType((t) => t.startsWith("video/webm"))?.label).toBe("WebM");
  });
});
