import { useRef } from "react";
import {
  NOT_IN_BILL_LABEL, VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
  periodLabel, standingLabel, unpricedLabel,
} from "../model/buildVideoCopy";
import { centreLegend, hex } from "../model/costPaint";
import { unplacedLegend } from "../model/ifcPaint";
import { useBuildStage } from "./useBuildStage";

/**
 * The stage a video is rendered from, headlessly.
 *
 * <b>Why this exists instead of screenshotting the real tab.</b> A recorder that drives the product
 * UI is hostage to it: move a panel, rename a button, and the video breaks or silently captures the
 * wrong thing. This is a separate surface with one job — put the model and its caption on screen at
 * a fixed size, and expose a function that renders one exact frame and resolves when it is done.
 *
 * Reached at <code>/?render=1</code> and driven by `tools/render_build_video/render.mjs`, which
 * screenshots the whole document. That is why the overlay here can be DOM: the CLI composites it for
 * free. The in-app recorder cannot, which is why the same wording is redrawn into a canvas from
 * `buildVideoCopy` — see `model/videoCompositor.ts`.
 */

/** Fixed stage. The exporter sets the window to match, so nothing is scaled or cropped later. */
const STAGE_W = 1600;
const STAGE_H = 900;

export function RenderHarness() {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const { meta, caption, error } = useBuildStage(hostRef, { publishGlobal: true });

  return (
    <div className="render-stage" style={{ width: STAGE_W, height: STAGE_H }}>
      <div className="render-canvas" ref={hostRef} data-render-canvas />

      {error && <div className="render-error" data-render-error>{error}</div>}

      <div className="render-overlay">
        <div className="render-heading">
          <div className="render-title">{VIDEO_TITLE}</div>
          <div className="render-sub">{VIDEO_SUBTITLE}</div>
        </div>

        {caption && meta && (
          <div className="render-stats" data-render-stats>
            <div className="render-period">{periodLabel(caption.period)}</div>
            <div className="render-count">{standingLabel(caption.built, meta.mapped)}</div>
            <div className="render-gap">{unpricedLabel(meta.unpriced)}</div>
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
  );
}
