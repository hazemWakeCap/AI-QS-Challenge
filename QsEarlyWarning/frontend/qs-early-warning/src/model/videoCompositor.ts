import {
  NOT_IN_BILL_LABEL, VIDEO_CAPTION_BODY, VIDEO_CAPTION_LEAD, VIDEO_SUBTITLE, VIDEO_TITLE,
  periodLabel, standingLabel, unpricedLabel,
} from "./buildVideoCopy";
import { centreLegend, hex } from "./costPaint";
import { unplacedLegend } from "./ifcPaint";

/**
 * Draws a complete video frame: the model, plus everything around it.
 *
 * <b>Why this exists.</b> `canvas.captureStream()` records the WebGL canvas and nothing else. On the
 * headless path that is fine, because the CLI screenshots the whole document and picks up the DOM
 * overlay for free. In the browser there is no such luxury — a recording of the raw canvas would be
 * the building floating on transparency, with no period, no legend and, worst of all, no caption.
 * The caption is the line that stops the sequence being read as a claim the data does not support,
 * so it cannot be the thing that quietly falls off the end of the pipeline.
 *
 * Every string is imported rather than written here. Two hand-maintained copies of the same sentence
 * is exactly how a video ends up disagreeing with the app it came from.
 */

export interface OverlayState {
  period: number;
  built: number;
  mapped: number;
  unpriced: number;
}

const W = 1600;
const H = 900;

// Mirrors the .render-* rules in styles.css. Kept as numbers because Canvas2D has no cascade —
// if the stylesheet moves, this is the other half that has to move with it.
const PAD_X = 48;
const PAD_Y = 40;
const LEFT_CLEAR = 200;   // clears the That Open attribution mark in the bottom-left
const INK = "#16203a";
const INK_SOFT = "#5a6884";
const INK_FAINT = "#8a97b1";
const WARN = "#d99a1c";

const UI = `'Inter Variable', ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`;
const DISPLAY = `'Space Grotesk Variable', ${UI}`;

export interface Compositor {
  canvas: HTMLCanvasElement;
  draw(gl: HTMLCanvasElement, state: OverlayState): void;
}

export function createCompositor(): Compositor {
  const canvas = document.createElement("canvas");
  canvas.width = W;
  canvas.height = H;
  const ctx = canvas.getContext("2d", { alpha: false });
  if (!ctx) throw new Error("could not get a 2D context to composite video frames into");

  return {
    canvas,
    draw(gl, state) {
      drawBackground(ctx);
      // The WebGL canvas is transparent (the scene has no background and the context is alpha), so
      // it composites over the gradient rather than replacing it.
      if (gl.width > 0 && gl.height > 0) ctx.drawImage(gl, 0, 0, W, H);
      drawHeading(ctx);
      drawStats(ctx, state);
      drawLegend(ctx);
      drawCaption(ctx);
    },
  };
}

function drawBackground(ctx: CanvasRenderingContext2D) {
  // The same 160° gradient as `.render-stage`, expressed as the equivalent linear ramp.
  const g = ctx.createLinearGradient(0, 0, W * 0.34, H);
  g.addColorStop(0, "#f7f9fc");
  g.addColorStop(1, "#eef2f8");
  ctx.fillStyle = g;
  ctx.fillRect(0, 0, W, H);
}

function drawHeading(ctx: CanvasRenderingContext2D) {
  ctx.textAlign = "left";
  ctx.textBaseline = "top";

  ctx.font = `600 30px ${DISPLAY}`;
  ctx.fillStyle = INK;
  ctx.fillText(VIDEO_TITLE, PAD_X, PAD_Y);

  ctx.font = `15px ${UI}`;
  ctx.fillStyle = INK_SOFT;
  ctx.fillText(VIDEO_SUBTITLE, PAD_X, PAD_Y + 44);
}

function drawStats(ctx: CanvasRenderingContext2D, s: OverlayState) {
  const right = W - PAD_X;
  ctx.textAlign = "right";
  ctx.textBaseline = "top";

  ctx.font = `600 44px ${DISPLAY}`;
  ctx.fillStyle = INK;
  ctx.fillText(periodLabel(s.period), right, PAD_Y);

  ctx.font = `15px ${UI}`;
  ctx.fillStyle = INK_SOFT;
  ctx.fillText(standingLabel(s.built, s.mapped), right, PAD_Y + 56);

  ctx.font = `13px ${UI}`;
  ctx.fillStyle = INK_FAINT;
  ctx.fillText(unpricedLabel(s.unpriced), right, PAD_Y + 80);
}

function drawLegend(ctx: CanvasRenderingContext2D) {
  const entries = [
    ...centreLegend().map((l) => ({ label: l.label, color: hex(l.color) })),
    { label: NOT_IN_BILL_LABEL, color: hex(unplacedLegend.color) },
  ];

  const y = H - 112;
  let x = LEFT_CLEAR;
  ctx.textAlign = "left";
  ctx.textBaseline = "middle";
  ctx.font = `13px ${UI}`;

  for (const e of entries) {
    ctx.fillStyle = e.color;
    ctx.fillRect(x, y - 6, 12, 12);
    ctx.fillStyle = INK_SOFT;
    ctx.fillText(e.label, x + 18, y);
    x += 18 + ctx.measureText(e.label).width + 16;
  }
}

function drawCaption(ctx: CanvasRenderingContext2D) {
  const x = LEFT_CLEAR;
  const top = H - 76;
  const maxWidth = W - x - PAD_X;

  // The amber rule down the left, matching `.render-caption`'s border.
  ctx.fillStyle = WARN;
  ctx.fillRect(x - 14, top - 4, 3, 44);

  ctx.textAlign = "left";
  ctx.textBaseline = "top";
  ctx.font = `13px ${UI}`;

  // The lead is bold and the body follows it inline, so measure the lead to know where to continue.
  ctx.font = `700 13px ${UI}`;
  const leadWidth = ctx.measureText(VIDEO_CAPTION_LEAD).width;

  const lines = wrap(ctx, VIDEO_CAPTION_BODY, maxWidth, leadWidth + 5, `13px ${UI}`);

  ctx.font = `700 13px ${UI}`;
  ctx.fillStyle = INK;
  ctx.fillText(VIDEO_CAPTION_LEAD, x, top);

  ctx.font = `13px ${UI}`;
  ctx.fillStyle = INK_SOFT;
  lines.forEach((line, i) => {
    ctx.fillText(line, i === 0 ? x + leadWidth + 5 : x, top + i * 20);
  });
}

/**
 * Greedy word wrap. `firstIndent` reserves room on the first line for the bold lead that precedes it.
 *
 * Canvas2D has no text layout, so wrapping is ours to do; getting it wrong shows up as a caption
 * running off the frame, which is precisely the sentence that must stay readable.
 */
function wrap(
  ctx: CanvasRenderingContext2D,
  text: string,
  maxWidth: number,
  firstIndent: number,
  font: string,
): string[] {
  ctx.font = font;
  const words = text.split(" ");
  const lines: string[] = [];
  let line = "";
  let budget = maxWidth - firstIndent;

  for (const word of words) {
    const candidate = line ? `${line} ${word}` : word;
    if (ctx.measureText(candidate).width <= budget) {
      line = candidate;
    } else {
      lines.push(line);
      line = word;
      budget = maxWidth;
    }
  }
  if (line) lines.push(line);
  return lines;
}
