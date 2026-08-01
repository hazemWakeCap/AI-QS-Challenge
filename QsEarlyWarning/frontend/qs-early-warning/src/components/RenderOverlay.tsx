import {
  NOT_IN_BILL_LABEL, VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
  budgetCells, modelScopeLabel, periodLabel, projectHeading, risingDeltaLabel, risingHeading,
  risingMoreLabel, risingPctLabel, standingLabel, unpricedLabel,
} from "../model/buildVideoCopy";
import { centreLegend, colorForCentreAlert, hex } from "../model/costPaint";
import { unplacedLegend } from "../model/ifcPaint";
import type { StageCaption, StageMeta } from "./useBuildStage";

/**
 * Everything drawn over the model on the build-sequence stage.
 *
 * <b>One component, two hosts.</b> The headless harness at <code>/?render=1</code> and the in-app
 * Build Video panel both mount this, so the recorded frame and the previewed frame cannot come to
 * disagree by someone editing one of them. The third copy — the canvas compositor — cannot share
 * JSX, because `captureStream()` sees only the WebGL canvas; it redraws the same strings from
 * `buildVideoCopy`, which is why every word here is imported rather than typed.
 */

export function RenderOverlay({ meta, caption }: { meta: StageMeta | null; caption: StageCaption | null }) {
  const readout = caption?.readout ?? null;

  return (
    <div className="render-overlay">
      <div className="render-heading">
        <div className="render-title">{VIDEO_TITLE}</div>
        <div className="render-sub">{VIDEO_SUBTITLE}</div>
      </div>

      {caption && meta && (
        <div className="render-stats" data-render-stats>
          <div className="render-period">{periodLabel(caption.period)}</div>
          {caption.month && <div className="render-month">{caption.month}</div>}
          <div className="render-count">{standingLabel(caption.built, meta.mapped)}</div>
          <div className="render-gap">{unpricedLabel(meta.unpriced)}</div>
        </div>
      )}

      {/* What is going up right now, and which trade owns it. */}
      {readout && meta && (
        <div className="render-rising" data-render-rising>
          <div className="render-block-head">{risingHeading(readout.disciplines)}</div>
          {readout.rising.map((c) => (
            <div key={c.bccId} className="render-rising-row">
              <i style={{ background: hex(colorForCentreAlert(c.alertLevel)) }} aria-hidden="true" />
              <span className="render-rising-id">{c.bccId}</span>
              <span className="render-rising-pkg">{c.packageCode}</span>
              <span className="render-rising-pct">{risingPctLabel(c.actualPct)}</span>
              <span className="render-rising-delta">{risingDeltaLabel(c.deltaPp)}</span>
            </div>
          ))}
          <div className="render-block-note">
            {risingMoreLabel(readout.risingMore, readout.projectMoving)}
          </div>
          <div className="render-block-note">
            {modelScopeLabel(readout.onModelBac, readout.totals.bac)}
          </div>
        </div>
      )}

      {/* Where the money and the programme stand at the same period — the whole project, not the
          slice of it on screen. */}
      {readout && meta && (
        <div className="render-strip" data-render-strip>
          <div className="render-block-head">{projectHeading(readout.period, meta.maxPeriod)}</div>
          <div className="render-cells">
            {budgetCells(readout).map((cell) => (
              <div key={cell.label} className="render-cell">
                <div className="render-cell-label">{cell.label}</div>
                <div className="render-cell-value">{cell.value}</div>
                <div className="render-cell-note">{cell.note}</div>
              </div>
            ))}
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
  );
}
