/**
 * Records a canvas to a video file, one pushed frame at a time.
 *
 * <b>Why frames are pushed rather than sampled.</b> `captureStream(fps)` samples on a wall clock, so
 * a frame that takes longer than its slot is dropped or duplicated — and a build frame takes as long
 * as the model needs to settle, which is not constant. With `captureStream(0)` nothing is sampled:
 * every frame in the output is one we drew and handed over, so a slow machine produces a slower
 * render rather than a video with holes in it.
 *
 * Verified by decoding the output outside the browser: 1600×900 H.264, correct duration, overlay and
 * caption present in the frames.
 */

/**
 * Container preference, best first.
 *
 * mp4 leads because it travels: it matches what the CLI produces and opens in anything. webm is the
 * fallback, since `MediaRecorder` support for mp4 is recent and uneven. Which one a browser will
 * actually give us is a question only that browser can answer, so this asks rather than assumes.
 */
const PREFERRED_TYPES = [
  "video/mp4;codecs=avc1.42E01E",
  "video/mp4",
  "video/webm;codecs=vp9",
  "video/webm;codecs=vp8",
  "video/webm",
];

export interface PickedType {
  mimeType: string;
  /** For naming the download and telling the user what they got. */
  extension: "mp4" | "webm";
  label: string;
}

/**
 * The best container this browser will record, or null if it will not record video at all.
 *
 * Exported for its own sake so the panel can say what it is about to produce before spending
 * fifteen seconds producing it.
 */
export function pickMimeType(
  isSupported: (type: string) => boolean = (t) =>
    typeof MediaRecorder !== "undefined" && MediaRecorder.isTypeSupported(t),
): PickedType | null {
  for (const mimeType of PREFERRED_TYPES) {
    if (!isSupported(mimeType)) continue;
    const isMp4 = mimeType.startsWith("video/mp4");
    return {
      mimeType,
      extension: isMp4 ? "mp4" : "webm",
      label: isMp4 ? "MP4 (H.264)" : "WebM",
    };
  }
  return null;
}

export interface Recording {
  /** Hands the canvas's current contents to the recorder as exactly one frame. */
  pushFrame(): void;
  /** Stops and resolves with the finished file. */
  stop(): Promise<Blob>;
  type: PickedType;
}

export function startRecording(canvas: HTMLCanvasElement): Recording {
  const type = pickMimeType();
  if (!type) throw new Error("This browser cannot record video from a canvas.");

  // 0 fps means "never sample on your own" — frames arrive only via requestFrame below.
  const stream = canvas.captureStream(0);
  const track = stream.getVideoTracks()[0] as CanvasCaptureMediaStreamTrack | undefined;
  if (!track) throw new Error("The canvas produced no video track to record.");

  const chunks: Blob[] = [];
  const recorder = new MediaRecorder(stream, { mimeType: type.mimeType, videoBitsPerSecond: 8_000_000 });
  recorder.ondataavailable = (e) => { if (e.data.size > 0) chunks.push(e.data); };
  // A timeslice keeps encoded data flowing instead of arriving in one lump at the end, which is what
  // makes the drain below short and predictable.
  recorder.start(200);

  return {
    type,
    pushFrame() {
      // `requestFrame` is what makes the stream advance; without it the recording is one still.
      track.requestFrame?.();
    },
    async stop() {
      // Frames handed over are encoded asynchronously. Stopping the moment the last one is pushed
      // truncates the tail — an early version ended on period 11 of 12, losing precisely the
      // finished building the video exists to arrive at.
      await new Promise((r) => setTimeout(r, TAIL_DRAIN_MS));

      return new Promise<Blob>((resolve, reject) => {
        recorder.onstop = () => {
          stream.getTracks().forEach((t) => t.stop());
          resolve(new Blob(chunks, { type: type.mimeType }));
        };
        recorder.onerror = (e) => reject(new Error(`recording failed: ${String(e)}`));
        recorder.requestData();
        recorder.stop();
      });
    },
  };
}

/** How long to let the encoder catch up after the final frame. Generous; it only costs the tail. */
const TAIL_DRAIN_MS = 600;

/** `CanvasCaptureMediaStreamTrack` is not in every lib.dom, and only `requestFrame` is needed. */
interface CanvasCaptureMediaStreamTrack extends MediaStreamTrack {
  requestFrame?: () => void;
}
