import { useEffect, useRef, useState } from "react";
import * as OBC from "@thatopen/components";
import * as FRAGS from "@thatopen/fragments";
import * as THREE from "three";
import { api, type CostCentreEvm } from "../api/client";
import { centreLegend, hex } from "../model/costPaint";
import { poseAt } from "../model/cameraPath";
import { buildElementIndex, type ElementMapIndex } from "../model/ifcElementMap";
import { fetchBundledIfc, loadIfc } from "../model/ifcLoader";
import { measureModel } from "../model/ifcMeasure";
import { paintSequenceFrame, unplacedLegend } from "../model/ifcPaint";
import { buildSequence, frameAt, type BuildSequence, type SequenceFrame } from "../model/ifcSequence";
import { createViewer, type Viewer } from "../model/viewer";

/**
 * The stage a video is rendered from.
 *
 * <b>Why this exists instead of screenshotting the real tab.</b> A recorder that drives the product
 * UI is hostage to it: move a panel, rename a button, and the video breaks or silently captures the
 * wrong thing. This is a separate surface with one job — put the model and its caption on screen at
 * a fixed size, and expose a function that renders one exact frame and resolves when it is done.
 *
 * Reached at <code>/?render=1</code>. Everything it draws comes from the same modules the IFC
 * Take-off tab uses, so the video cannot drift from what the product shows.
 *
 * The contract, published on <code>window.__qsRender</code>, is deliberately tiny:
 * <pre>
 *   await window.__qsRender.ready                 // metadata once the model is loaded
 *   await window.__qsRender.renderFrame(t, camT)  // resolves when the frame is on screen
 * </pre>
 */

export interface RenderApi {
  ready: Promise<RenderMeta>;
  renderFrame(t: number, cameraT: number): Promise<RenderFrameInfo>;
}

export interface RenderMeta {
  minPeriod: number;
  maxPeriod: number;
  mapped: number;
  total: number;
  unpriced: number;
}

export interface RenderFrameInfo {
  period: number;
  built: number;
  elapsedMs: number;
}

/** Fixed stage. The exporter sets the window to match, so nothing is scaled or cropped later. */
const STAGE_W = 1600;
const STAGE_H = 900;

export function RenderHarness() {
  const hostRef = useRef<HTMLDivElement | null>(null);
  const [meta, setMeta] = useState<RenderMeta | null>(null);
  const [caption, setCaption] = useState<{ period: number; built: number } | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    let cancelled = false;
    let owned: Viewer | null = null;
    let resolveReady: (m: RenderMeta) => void = () => {};
    let rejectReady: (e: unknown) => void = () => {};

    const ready = new Promise<RenderMeta>((res, rej) => { resolveReady = res; rejectReady = rej; });

    // State the frame function closes over. Refs would be indirection for no benefit — this effect
    // owns the whole lifecycle and nothing else writes them.
    let viewer: Viewer | null = null;
    let model: FRAGS.FragmentsModel | null = null;
    let index: ElementMapIndex | null = null;
    let sequence: BuildSequence | null = null;
    let centresByPeriod: Map<number, CostCentreEvm[]> | null = null;
    let previous: SequenceFrame | null = null;
    let box = new THREE.Box3();

    const renderFrame = async (t: number, cameraT: number): Promise<RenderFrameInfo> => {
      const started = performance.now();
      if (!viewer || !model || !index || !sequence || !centresByPeriod) {
        throw new Error("renderFrame called before the harness was ready");
      }

      // Rewinding must not be a special case: the exporter may render frames in any order, and a
      // delta against a later frame would leave elements standing that should not be.
      if (previous && t < previousT) previous = null;
      previousT = t;

      const frame = frameAt(t, sequence, centresByPeriod);
      await paintSequenceFrame(viewer, model, index, frame, previous, /* waitForSettle */ true);
      previous = frame;

      const pose = poseAt(box, cameraT);
      await viewer.world.camera.controls?.setLookAt(
        pose.position.x, pose.position.y, pose.position.z,
        pose.target.x, pose.target.y, pose.target.z,
        false,
      );

      // MANUAL mode means nothing is drawn until asked, so the pixels are known to correspond to
      // the state above rather than to whatever the last animation frame happened to catch.
      const renderer = viewer.world.renderer!;
      renderer.needsUpdate = true;
      renderer.update();

      setCaption({ period: Math.floor(t), built: frame.builtCount });
      return { period: Math.floor(t), built: frame.builtCount, elapsedMs: Math.round(performance.now() - started) };
    };

    let previousT = -Infinity;

    (async () => {
      try {
        const v = await createViewer(host);
        if (cancelled) { v.dispose(); return; }
        owned = v;
        viewer = v;

        // Draw only when told to. In AUTO the renderer redraws every animation frame, so a capture
        // could land between a data change and its repaint.
        v.world.renderer!.mode = OBC.RendererMode.MANUAL;

        // `viewer.ts` refreshes the whole model whenever the camera settles. That is right for a
        // person orbiting and ruinous here, where the camera moves on every single frame.
        v.world.camera.controls?.removeAllEventListeners?.("rest");

        const bytes = await fetchBundledIfc();
        if (cancelled) return;
        const m = await loadIfc(v, bytes);
        model = m;

        // Full geometry regardless of camera distance — the orbit must not change what is drawn.
        await m.setLodMode(FRAGS.LodMode.ALL_GEOMETRY);

        const boxes = await m.getBoxes();
        if (boxes?.length) {
          const merged = new THREE.Box3();
          for (const b of boxes) merged.union(b);
          box = merged;
        }

        const measured = await measureModel(m);
        const map = await api.elementMap();
        index = await buildElementIndex(m, map);
        sequence = buildSequence(index);

        const cm = await api.costMap();
        const periods: number[] = [];
        for (let p = cm.minPeriod; p <= cm.maxPeriod; p++) periods.push(p);
        const rows = await Promise.all(
          periods.map((p): Promise<[number, CostCentreEvm[]]> =>
            api.costCentres(p).then((r) => [p, r] as [number, CostCentreEvm[]])),
        );
        centresByPeriod = new Map(rows);

        const info: RenderMeta = {
          minPeriod: cm.minPeriod,
          maxPeriod: cm.maxPeriod,
          mapped: index.mappedLocalIds.length,
          total: measured.report.totalElements,
          unpriced: index.unmappedLocalIds.length,
        };
        if (cancelled) return;
        setMeta(info);
        resolveReady(info);
      } catch (e) {
        if (!cancelled) {
          setError(String((e as Error).message ?? e));
          rejectReady(e);
        }
      }
    })();

    (window as unknown as { __qsRender: RenderApi }).__qsRender = { ready, renderFrame };

    return () => {
      cancelled = true;
      delete (window as unknown as { __qsRender?: RenderApi }).__qsRender;
      owned?.dispose();
      host.querySelectorAll("canvas").forEach((c) => c.remove());
    };
  }, []);

  return (
    <div className="render-stage" style={{ width: STAGE_W, height: STAGE_H }}>
      <div className="render-canvas" ref={hostRef} data-render-canvas />

      {error && <div className="render-error" data-render-error>{error}</div>}

      <div className="render-overlay">
        <div className="render-heading">
          <div className="render-title">Tower X · build sequence</div>
          <div className="render-sub">
            Structure rises at each cost centre&apos;s recorded progress · coloured by that
            centre&apos;s alert level
          </div>
        </div>

        {caption && meta && (
          <div className="render-stats" data-render-stats>
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
            Not in the bill
          </span>
        </div>

        <div className="render-caption">
          <b>The order is assumed, the amounts are not.</b> The sheet records percent complete per
          cost centre, never per element, so elements rise bottom-up within their trade while the
          pace and the colour come from the workbook.
        </div>
      </div>
    </div>
  );
}
