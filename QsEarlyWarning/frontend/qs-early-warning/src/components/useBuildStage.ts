import { useEffect, useRef, useState } from "react";
import * as OBC from "@thatopen/components";
import * as FRAGS from "@thatopen/fragments";
import * as THREE from "three";
import { api, type CostCentreEvm } from "../api/client";
import { monthLabel } from "../model/buildVideoCopy";
import { frameReadout, type FrameReadout } from "../model/buildVideoStats";
import { poseAt } from "../model/cameraPath";
import { buildElementIndex, type ElementMapIndex } from "../model/ifcElementMap";
import { fetchBundledIfc, loadIfc } from "../model/ifcLoader";
import { measureModel } from "../model/ifcMeasure";
import { paintSequenceFrame } from "../model/ifcPaint";
import { buildSequence, frameAt, type BuildSequence, type SequenceFrame } from "../model/ifcSequence";
import { createViewer, type Viewer } from "../model/viewer";

/**
 * The build-sequence stage: a loaded model that can render one exact frame on demand.
 *
 * Shared by the two things that need it — the headless page at <code>/?render=1</code>, where a CLI
 * screenshots the whole document, and the in-app Build Video panel, which composites and records the
 * canvas itself. Both must show the same thing; the surest way to guarantee that is for both to be
 * the same code.
 *
 * The stage is deliberately not interactive. The renderer is put in MANUAL mode so nothing is drawn
 * except when a frame is asked for, and the camera's settle-triggered refresh is detached — both
 * would otherwise let pixels change between deciding what a frame contains and capturing it.
 */

export interface StageMeta {
  minPeriod: number;
  maxPeriod: number;
  mapped: number;
  total: number;
  unpriced: number;
}

/** Everything one frame states about itself, beyond the pixels. */
export interface StageCaption {
  period: number;
  /** The calendar month the period stands for, when the workbook gave one. */
  month: string | null;
  built: number;
  /**
   * What is rising at this period and where the project's money stands.
   *
   * Recomputed per frame rather than per period: it is a walk over ~173 rows twice, which is
   * nothing beside painting the model, and tying it to the frame means it can never lag the picture.
   */
  readout: FrameReadout | null;
}

export interface StageFrameInfo extends StageCaption {
  elapsedMs: number;
}

export interface BuildStage {
  meta: StageMeta | null;
  /** Resolves once the requested frame is settled and drawn. */
  renderFrame: (t: number, cameraT: number) => Promise<StageFrameInfo>;
  /** The WebGL canvas, once it exists. What a recorder composites from. */
  glCanvas: () => HTMLCanvasElement | null;
  /** Latest frame's caption numbers, for a DOM overlay to display. */
  caption: StageCaption | null;
  error: string | null;
}

export interface BuildStageOptions {
  /**
   * Publish `window.__qsRender` for the headless CLI.
   *
   * Off by default: it is a single global, so two mounted stages would have the second clobber the
   * first and the first's cleanup delete it out from under the renderer.
   */
  publishGlobal?: boolean;
  /** Ask the WebGL context to keep its buffer, which `drawImage` needs outside the render task. */
  preserveDrawingBuffer?: boolean;
}

export function useBuildStage(
  hostRef: React.RefObject<HTMLDivElement | null>,
  { publishGlobal = false, preserveDrawingBuffer = false }: BuildStageOptions = {},
): BuildStage {
  const [meta, setMeta] = useState<StageMeta | null>(null);
  const [caption, setCaption] = useState<StageCaption | null>(null);
  const [error, setError] = useState<string | null>(null);

  // The frame function is created inside the effect and reached through a ref, so consumers get a
  // stable identity and never re-run their own effects when a frame is drawn.
  const renderRef = useRef<((t: number, cameraT: number) => Promise<StageFrameInfo>) | null>(null);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);

  useEffect(() => {
    const host = hostRef.current;
    if (!host) return;

    let cancelled = false;
    let owned: Viewer | null = null;
    let resolveReady: (m: StageMeta) => void = () => {};
    let rejectReady: (e: unknown) => void = () => {};
    const ready = new Promise<StageMeta>((res, rej) => { resolveReady = res; rejectReady = rej; });
    // A rejected promise nobody is listening to is an unhandled rejection; the harness may never be
    // asked for `ready` if the CLI is not attached.
    ready.catch(() => {});

    let viewer: Viewer | null = null;
    let model: FRAGS.FragmentsModel | null = null;
    let index: ElementMapIndex | null = null;
    let sequence: BuildSequence | null = null;
    let centresByPeriod: Map<number, CostCentreEvm[]> | null = null;
    /** The cost centres the sequence can actually show — what "rising now" is scoped to. */
    let onModel: Set<string> = new Set();
    let monthByPeriod: Map<number, string> = new Map();
    let previous: SequenceFrame | null = null;
    let previousT = -Infinity;
    let box = new THREE.Box3();

    const renderFrame = async (t: number, cameraT: number): Promise<StageFrameInfo> => {
      const started = performance.now();
      if (!viewer || !model || !index || !sequence || !centresByPeriod) {
        throw new Error("renderFrame called before the stage was ready");
      }

      // Rewinding must not be a special case: frames may be requested in any order, and a delta
      // against a later frame would leave elements standing that should not be.
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

      const renderer = viewer.world.renderer!;
      renderer.needsUpdate = true;
      renderer.update();

      // The readout quotes the period's reported figures, so it reads the floor rather than the
      // fractional position: a month's CPI is a month's CPI, and interpolating one would invent a
      // number the workbook never states. The geometry alone moves continuously.
      const period = Math.floor(t);
      const info: StageCaption = {
        period,
        month: monthByPeriod.get(period) ?? null,
        built: frame.builtCount,
        readout: frameReadout(period, centresByPeriod, onModel),
      };

      setCaption(info);
      return { ...info, elapsedMs: Math.round(performance.now() - started) };
    };
    renderRef.current = renderFrame;

    (async () => {
      try {
        const v = await createViewer(host, preserveDrawingBuffer ? { preserveDrawingBuffer: true } : undefined);
        if (cancelled) { v.dispose(); return; }
        owned = v;
        viewer = v;
        canvasRef.current = host.querySelector("canvas");

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
        canvasRef.current = host.querySelector("canvas");

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
        onModel = new Set(sequence.keys());

        const cm = await api.costMap();
        const periods: number[] = [];
        for (let p = cm.minPeriod; p <= cm.maxPeriod; p++) periods.push(p);
        const rows = await Promise.all(
          periods.map((p): Promise<[number, CostCentreEvm[]]> =>
            api.costCentres(p).then((r) => [p, r] as [number, CostCentreEvm[]])),
        );
        centresByPeriod = new Map(rows);

        // The calendar month behind each period. A missing or unparseable date costs the overlay one
        // line, never the frame, so a failure here is swallowed rather than allowed to stop a render.
        const calendar = await api.periods().catch(() => []);
        monthByPeriod = new Map(
          calendar
            .map((p) => [p.period, monthLabel(p.periodStart)] as const)
            .filter((e): e is [number, string] => e[1] !== null),
        );

        const info: StageMeta = {
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

    if (publishGlobal) {
      (window as unknown as { __qsRender: unknown }).__qsRender = {
        ready,
        renderFrame: (t: number, cameraT: number) => renderFrame(t, cameraT),
      };
    }

    return () => {
      cancelled = true;
      renderRef.current = null;
      canvasRef.current = null;
      if (publishGlobal) delete (window as unknown as { __qsRender?: unknown }).__qsRender;
      owned?.dispose();
      host.querySelectorAll("canvas").forEach((c) => c.remove());
      // The That Open attribution is a sibling div, not a canvas, so it survives the line above and
      // would stack up on every remount.
      host.querySelectorAll("[data-thatopen-logo]").forEach((el) => el.remove());
    };
  }, [hostRef, publishGlobal, preserveDrawingBuffer]);

  return {
    meta,
    caption,
    error,
    glCanvas: () => canvasRef.current,
    renderFrame: (t, cameraT) => {
      const fn = renderRef.current;
      if (!fn) return Promise.reject(new Error("stage is not mounted"));
      return fn(t, cameraT);
    },
  };
}
