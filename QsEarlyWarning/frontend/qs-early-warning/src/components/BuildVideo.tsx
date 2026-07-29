import { useEffect, useRef, useState } from "react";
import { timeline } from "../model/cameraPath";
import {
  NOT_IN_BILL_LABEL, VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
} from "../model/buildVideoCopy";
import { centreLegend, hex } from "../model/costPaint";
import { unplacedLegend } from "../model/ifcPaint";
import { createCompositor } from "../model/videoCompositor";
import { pickMimeType, startRecording } from "../model/videoRecorder";
import { Spinner } from "./Loading";
import { useBuildStage } from "./useBuildStage";

/**
 * Build Video — render the construction sequence and watch it back, without leaving the app.
 *
 * The same stage the headless CLI drives, recorded in the browser instead. What the CLI gets for
 * free by screenshotting a whole page, this has to do deliberately: the WebGL canvas is the only
 * thing a capture stream can see, so each frame is composited with its overlay before being handed
 * to the recorder. See `model/videoCompositor.ts`.
 *
 * The stage renders at a fixed 1600×900 whatever size it is displayed at — the preview is scaled
 * down for the page, the recording is not.
 */

const STAGE_W = 1600;
const STAGE_H = 900;

/** Extra copies of the closing frame, so the completed building holds for about a second. */
const HOLD_FRAMES = 30;

/** 240 frames at 30fps is the eight seconds the published video runs to. */
const FRAME_CHOICES = [
  { frames: 120, fps: 30, label: "4s · quick" },
  { frames: 240, fps: 30, label: "8s · full" },
];

export function BuildVideo() {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const stageRef = useRef<HTMLDivElement | null>(null);
  const { meta, caption, error, renderFrame } = useBuildStage(hostRef, {
    // The compositor reads the WebGL canvas outside the render task, which returns a cleared buffer
    // unless the context was asked to keep it.
    preserveDrawingBuffer: true,
  });

  const [choice, setChoice] = useState(FRAME_CHOICES[1]);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState("");
  const [videoUrl, setVideoUrl] = useState<string | null>(null);
  const [videoExt, setVideoExt] = useState<"mp4" | "webm">("webm");
  const [failure, setFailure] = useState<string | null>(null);
  const [scale, setScale] = useState(1);

  const codec = pickMimeType();

  // The preview is a fixed 1600×900 box scaled to whatever width the card gives it. Scaling rather
  // than resizing keeps the recorded frame identical to the CLI's regardless of the browser window.
  useEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const fit = () => setScale(Math.min(1, el.clientWidth / STAGE_W));
    fit();
    const ro = new ResizeObserver(fit);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // An object URL is a live handle on a multi-megabyte blob, so it has to be released — but NOT from
  // an effect keyed on the URL. StrictMode mounts an effect, immediately runs its cleanup, then
  // mounts it again; keyed that way it revoked the URL the instant it was created and left the
  // player pointing at a dead blob. Held in a ref and released on real unmount, or when replaced.
  const urlRef = useRef<string | null>(null);
  useEffect(() => () => { if (urlRef.current) URL.revokeObjectURL(urlRef.current); }, []);

  async function generate() {
    if (!meta) return;
    setBusy(true);
    setFailure(null);
    setStatus("Preparing…");

    try {
      const gl = hostRef.current?.querySelector("canvas");
      if (!gl) throw new Error("The 3D view is not ready yet.");

      const compositor = createCompositor();
      const recording = startRecording(compositor.canvas);
      setVideoExt(recording.type.extension);

      const times = timeline(choice.frames, meta.minPeriod, meta.maxPeriod);
      for (let i = 0; i < times.length; i++) {
        const info = await renderFrame(times[i], i / (times.length - 1));
        compositor.draw(gl, {
          period: info.period,
          built: info.built,
          mapped: meta.mapped,
          unpriced: meta.unpriced,
        });
        recording.pushFrame();

        // Hold on the finished building. The last frame carries the whole point — the completed
        // structure and the scope that never filled in — and one frame of it goes by unread.
        if (i === times.length - 1) {
          for (let hold = 0; hold < HOLD_FRAMES; hold++) recording.pushFrame();
        }

        if (i % 5 === 0 || i === times.length - 1) {
          setStatus(`Rendering frame ${i + 1} of ${times.length} · period ${info.period}`);
        }
      }

      setStatus("Encoding…");
      const blob = await recording.stop();

      // Replace rather than accumulate: the previous recording is several megabytes. Done here and
      // not inside a state updater — updaters must be pure, and StrictMode runs them twice, which
      // would mint two URLs and leak one.
      if (urlRef.current) URL.revokeObjectURL(urlRef.current);
      urlRef.current = URL.createObjectURL(blob);
      setVideoUrl(urlRef.current);
      setStatus("");
    } catch (e) {
      setFailure(String((e as Error).message ?? e));
      setStatus("");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="buildvideo">
      <div className="panel-head">
        <span className="pill pill-blue">BUILD VIDEO</span>
        <span className="muted small">
          The 4D sequence, rendered here and playable in the page
          {codec ? ` · ${codec.label}` : ""}
        </span>
      </div>

      {error && <div className="error">{error}</div>}
      {failure && <div className="error">{failure}</div>}
      {!codec && (
        <div className="error">This browser cannot record video from a canvas.</div>
      )}

      <div className="buildvideo-controls">
        <div className="seg">
          {FRAME_CHOICES.map((c) => (
            <button
              key={c.frames}
              className={`btn btn-sm ${choice.frames === c.frames ? "btn-primary" : "btn-ghost"}`}
              disabled={busy}
              onClick={() => setChoice(c)}
            >
              {c.label}
            </button>
          ))}
        </div>

        <button
          className="btn btn-primary"
          disabled={busy || !meta || !codec}
          onClick={() => void generate()}
        >
          {busy ? "Rendering…" : videoUrl ? "Render again" : "Render video"}
        </button>

        {videoUrl && !busy && (
          <a className="btn btn-secondary" href={videoUrl} download={`tower-4d-build.${videoExt}`}>
            Download
          </a>
        )}
      </div>

      {/* The live stage. Always mounted — it is what the recorder composites from. */}
      <div className="buildvideo-stage" ref={stageRef}>
        <div
          className="render-stage"
          style={{
            width: STAGE_W, height: STAGE_H,
            transform: `scale(${scale})`, transformOrigin: "top left",
          }}
        >
          <div className="render-canvas" ref={hostRef} />

          <div className="render-overlay">
            <div className="render-heading">
              <div className="render-title">{VIDEO_TITLE}</div>
              <div className="render-sub">{VIDEO_SUBTITLE}</div>
            </div>

            {caption && meta && (
              <div className="render-stats">
                <div className="render-period">Period {caption.period}</div>
                <div className="render-count">
                  {caption.built.toLocaleString()} of {meta.mapped.toLocaleString()} priced elements standing
                </div>
                <div className="render-gap">
                  {meta.unpriced.toLocaleString()} elements the bill never priced — they never build
                </div>
              </div>
            )}

            <div className="render-legend">
              {centreLegend().map((l) => (
                <span key={l.label} className="legend-item">
                  <i style={{ background: hex(l.color) }} aria-hidden="true" />
                  {l.label}
                </span>
              ))}
              <span className="legend-item">
                <i style={{ background: hex(unplacedLegend.color) }} aria-hidden="true" />
                {NOT_IN_BILL_LABEL}
              </span>
            </div>

            <div className="render-caption">
              <b>{VIDEO_CAPTION_LEAD}</b> {VIDEO_CAPTION_BODY}
            </div>
          </div>
        </div>

        {(busy || !meta) && (
          <div className="model-loading">
            <Spinner />
            <p className="muted small">{status || "Loading the model…"}</p>
          </div>
        )}
      </div>

      {videoUrl && (
        <div className="buildvideo-player">
          <h3>Result</h3>
          <video src={videoUrl} controls autoPlay loop playsInline />
          <p className="muted small">
            {choice.frames} frames at {choice.fps}fps · 1600×900 · recorded in this browser.
            The deck's version is rendered headlessly and is byte-for-byte reproducible — run{" "}
            <span className="mono">tools/render_build_video/render.mjs</span> for that one.
          </p>
        </div>
      )}
    </div>
  );
}
