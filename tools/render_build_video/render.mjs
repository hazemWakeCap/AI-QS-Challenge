#!/usr/bin/env node
/**
 * Renders the 4D build sequence to video, headless and deterministically.
 *
 * WHY THIS EXISTS
 * ---------------
 * The first version of this video was made by hand: seven browser screenshots taken through a
 * remote-control tool, cropped and crossfaded with a one-off ffmpeg command. It could not be
 * reproduced, and it could not follow the data — reprice an item or add a period and the video was
 * quietly stale with no way to tell. This renders the same sequence from live data, on demand, at
 * whatever frame rate and resolution you ask for.
 *
 * DETERMINISM IS THE CONTRACT
 * ---------------------------
 * Run it twice, get byte-identical frames. That is not decoration: `buildSequence` sorts by storey
 * then GlobalId precisely so a rendered frame is a pure function of (data, frame index), and this
 * tool is what makes that claim testable. Nothing here is paced by a wall clock — each frame is
 * requested, awaited until the model reports it has finished redrawing, and only then captured.
 *
 * WHAT THIS IS NOT
 * ----------------
 * Not a screen recorder. It never drives the product UI, so moving a panel or renaming a button
 * cannot change or break the output. It talks to `window.__qsRender`, a small surface the app
 * publishes at /?render=1 for exactly this purpose.
 *
 * The build ORDER in the video is an assumption, not data — the sheet records percent complete per
 * cost centre, never per element. The video says so on every frame; see
 * docs/17-ifc-boq-element-map.md, assumption 8.
 *
 * HEADLESS WEBGL
 * --------------
 * Chrome's stripped `chrome-headless-shell` builds bundled with Playwright/Puppeteer crash creating
 * a WebGL context on this machine, which is why an earlier attempt concluded headless rendering was
 * impossible. It is not — the full Google Chrome app in `--headless=new` gets a real GPU. The
 * binary was the problem, never the flags.
 *
 * No dependencies. Node 22 ships a native WebSocket, so Chrome DevTools Protocol needs nothing else,
 * and ffmpeg is invoked as a subprocess.
 *
 * Usage:   node tools/render_build_video/render.mjs [--frames 240] [--fps 30] [--out PATH]
 *                                                   [--keep-frames] [--browser PATH] [--url URL]
 * Output:  presentation/tower-4d-build.mp4
 */

import { spawn, spawnSync } from "node:child_process";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

/** Chrome builds that can actually create a WebGL context, best first. */
const BROWSER_CANDIDATES = [
  "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
  `${process.env.HOME}/.cache/hyperframes/chrome/chrome-headless-shell/mac_arm-152.0.7928.2/chrome-headless-shell-mac-arm64/chrome-headless-shell`,
];

const STAGE = { width: 1600, height: 900 };

function parseArgs(argv) {
  const opts = {
    frames: 240,
    fps: 30,
    out: path.join(REPO, "presentation/tower-4d-build.mp4"),
    keepFrames: false,
    browser: null,
    url: "http://localhost:5173/?render=1",
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === "--frames") opts.frames = Number(argv[++i]);
    else if (a === "--fps") opts.fps = Number(argv[++i]);
    else if (a === "--out") opts.out = path.resolve(argv[++i]);
    else if (a === "--keep-frames") opts.keepFrames = true;
    else if (a === "--browser") opts.browser = argv[++i];
    else if (a === "--url") opts.url = argv[++i];
    else if (a === "--help" || a === "-h") { console.log(HELP); process.exit(0); }
    else { console.error(`unknown flag: ${a}\n${HELP}`); process.exit(2); }
  }
  if (!Number.isFinite(opts.frames) || opts.frames < 2) fail("--frames must be at least 2");
  if (!Number.isFinite(opts.fps) || opts.fps < 1) fail("--fps must be at least 1");
  return opts;
}

const HELP = `
render.mjs — render the 4D build sequence to video

  --frames N        frames to render (default 240)
  --fps N           output frame rate (default 30)
  --out PATH        output file (default presentation/tower-4d-build.mp4)
  --keep-frames     keep the PNG frames and print where they are (for determinism checks)
  --browser PATH    Chrome binary to use
  --url URL         harness URL (default http://localhost:5173/?render=1)
`.trim();

function fail(message, hint) {
  console.error(`\nerror: ${message}`);
  if (hint) console.error(`       ${hint}`);
  process.exit(1);
}

// ── preflight ────────────────────────────────────────────────────────────────

async function reachable(url) {
  try {
    const res = await fetch(url, { signal: AbortSignal.timeout(2500) });
    return res.ok || res.status === 404; // a 404 still proves something is listening
  } catch { return false; }
}

function which(bin) {
  const r = spawnSync("which", [bin], { encoding: "utf-8" });
  return r.status === 0 ? r.stdout.trim() : null;
}

function pickBrowser(override) {
  const candidates = override ? [override] : BROWSER_CANDIDATES;
  for (const c of candidates) {
    if (spawnSync("test", ["-x", c]).status === 0) return c;
  }
  return null;
}

// ── a minimal Chrome DevTools Protocol client ────────────────────────────────

class Cdp {
  #ws; #next = 1; #pending = new Map();

  static async connect(wsUrl) {
    const cdp = new Cdp();
    cdp.#ws = new WebSocket(wsUrl);
    await new Promise((resolve, reject) => {
      cdp.#ws.addEventListener("open", resolve, { once: true });
      cdp.#ws.addEventListener("error", () => reject(new Error(`could not attach to ${wsUrl}`)), { once: true });
    });
    cdp.#ws.addEventListener("message", (ev) => {
      const msg = JSON.parse(ev.data);
      const waiter = cdp.#pending.get(msg.id);
      if (!waiter) return;                       // an event, not a reply — nothing here needs events
      cdp.#pending.delete(msg.id);
      if (msg.error) waiter.reject(new Error(`${msg.error.message ?? "CDP error"}`));
      else waiter.resolve(msg.result);
    });
    return cdp;
  }

  send(method, params = {}, sessionId) {
    const id = this.#next++;
    const payload = JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) });
    return new Promise((resolve, reject) => {
      this.#pending.set(id, { resolve, reject });
      this.#ws.send(payload);
    });
  }

  close() { try { this.#ws.close(); } catch { /* already gone */ } }
}

/** Evaluates an async expression in the page and returns its value, surfacing page errors as ours. */
async function evaluate(cdp, sessionId, expression) {
  const res = await cdp.send("Runtime.evaluate", {
    expression, awaitPromise: true, returnByValue: true,
  }, sessionId);
  if (res.exceptionDetails) {
    const d = res.exceptionDetails;
    throw new Error(d.exception?.description ?? d.text ?? "page threw");
  }
  return res.result?.value;
}

// ── the render ───────────────────────────────────────────────────────────────

async function main() {
  const opts = parseArgs(process.argv.slice(2));

  if (!which("ffmpeg")) fail("ffmpeg is not installed", "brew install ffmpeg");

  const origin = new URL(opts.url).origin;
  if (!(await reachable(origin))) {
    fail(`no dev server at ${origin}`, "start the system first (see .claude/commands/run_system.md)");
  }
  if (!(await reachable("http://localhost:5070/api/v1/health"))) {
    fail("the API is not answering on :5070", "the harness reads live cost data and cannot render without it");
  }

  const browser = pickBrowser(opts.browser);
  if (!browser) {
    fail("no Chrome build found that can render WebGL headlessly",
         `tried:\n       ${BROWSER_CANDIDATES.join("\n       ")}`);
  }

  const profile = mkdtempSync(path.join(tmpdir(), "qs-render-"));
  const frameDir = mkdtempSync(path.join(tmpdir(), "qs-frames-"));

  console.log(`browser  ${browser}`);
  console.log(`harness  ${opts.url}`);
  console.log(`frames   ${opts.frames} @ ${opts.fps}fps  (${(opts.frames / opts.fps).toFixed(1)}s)`);
  console.log(`stage    ${STAGE.width}x${STAGE.height}`);

  const chrome = spawn(browser, [
    "--headless=new",
    "--remote-debugging-port=0",
    `--user-data-dir=${profile}`,
    `--window-size=${STAGE.width},${STAGE.height}`,
    "--hide-scrollbars",
    // Pin the device scale so a frame is the same pixels on any display the tool happens to run on.
    "--force-device-scale-factor=1",
    "--no-first-run",
    "--no-default-browser-check",
    "--disable-extensions",
  ], { stdio: ["ignore", "pipe", "pipe"] });

  let cdp = null;
  let exitCode = 0;
  try {
    const wsUrl = await new Promise((resolve, reject) => {
      let buf = "";
      const timer = setTimeout(() => reject(new Error("Chrome did not report a debugging port")), 20000);
      chrome.stderr.on("data", (d) => {
        buf += d.toString();
        const m = buf.match(/ws:\/\/[^\s]+/);
        if (m) { clearTimeout(timer); resolve(m[0]); }
      });
      chrome.on("exit", (code) => { clearTimeout(timer); reject(new Error(`Chrome exited with ${code}`)); });
    });

    cdp = await Cdp.connect(wsUrl);
    const { targetId } = await cdp.send("Target.createTarget", { url: "about:blank" });
    const { sessionId } = await cdp.send("Target.attachToTarget", { targetId, flatten: true });

    await cdp.send("Page.enable", {}, sessionId);
    await cdp.send("Runtime.enable", {}, sessionId);
    // Belt and braces over --window-size: this is what actually fixes the viewport.
    await cdp.send("Emulation.setDeviceMetricsOverride", {
      width: STAGE.width, height: STAGE.height, deviceScaleFactor: 1, mobile: false,
    }, sessionId);

    await cdp.send("Page.navigate", { url: opts.url }, sessionId);

    process.stdout.write("loading model… ");
    const meta = await evaluate(cdp, sessionId, `
      (async () => {
        const started = Date.now();
        while (!window.__qsRender) {
          if (Date.now() - started > 120000) throw new Error('harness never appeared at ' + location.href);
          await new Promise(r => setTimeout(r, 250));
        }
        return await window.__qsRender.ready;
      })()
    `);
    console.log(`ok — ${meta.mapped} priced of ${meta.total} elements, periods ${meta.minPeriod}–${meta.maxPeriod}`);

    const times = [];
    const timings = [];
    for (let i = 0; i < opts.frames; i++) {
      const u = i / (opts.frames - 1);
      const t = meta.minPeriod + (meta.maxPeriod - meta.minPeriod) * u;
      times.push(t);

      const info = await evaluate(cdp, sessionId,
        `window.__qsRender.renderFrame(${t}, ${u})`);
      timings.push(info.elapsedMs);

      const shot = await cdp.send("Page.captureScreenshot",
        { format: "png", captureBeyondViewport: false }, sessionId);
      writeFileSync(path.join(frameDir, `f${String(i).padStart(5, "0")}.png`),
        Buffer.from(shot.data, "base64"));

      if (i % 20 === 0 || i === opts.frames - 1) {
        process.stdout.write(`\rframe ${i + 1}/${opts.frames}  period ${info.period}  ${info.built} standing   `);
      }
    }
    console.log();

    const sorted = [...timings].sort((a, b) => a - b);
    console.log(`frame time  median ${sorted[Math.floor(sorted.length / 2)]}ms  max ${sorted.at(-1)}ms`);

    mkdirSync(path.dirname(opts.out), { recursive: true });
    const ff = spawnSync("ffmpeg", [
      "-y", "-loglevel", "error",
      "-framerate", String(opts.fps),
      "-i", path.join(frameDir, "f%05d.png"),
      // Frames are real renders, not stills, so nothing is interpolated. yuv420p for players that
      // refuse anything else; even dimensions guaranteed by the fixed stage.
      "-vf", "format=yuv420p",
      "-c:v", "libx264", "-crf", "20", "-preset", "slow",
      "-movflags", "+faststart",
      opts.out,
    ], { stdio: "inherit" });
    if (ff.status !== 0) fail("ffmpeg failed to encode the frames");

    console.log(`\nwrote ${path.relative(REPO, opts.out)}`);
    if (opts.keepFrames) console.log(`frames ${frameDir}`);
  } catch (err) {
    console.error(`\nerror: ${err.message}`);
    exitCode = 1;
  } finally {
    cdp?.close();
    chrome.kill();
    // Chrome flushes its profile on the way out, so deleting it immediately races those writes and
    // throws ENOTEMPTY. Wait for the process to actually be gone, then clean up.
    await new Promise((resolve) => {
      if (chrome.exitCode !== null) return resolve();
      const timer = setTimeout(resolve, 5000);
      chrome.once("exit", () => { clearTimeout(timer); resolve(); });
    });
    if (!opts.keepFrames) rmSync(frameDir, { recursive: true, force: true });
    rmSync(profile, { recursive: true, force: true });
  }
  process.exit(exitCode);
}

await main();
