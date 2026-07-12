import { useEffect, useState } from "react";
import { api, type CopilotTurn, type CopilotEvidence, type WatchlistRow } from "../api/client";
import { Watchlist } from "./Watchlist";
import { Sources } from "./Sources";

interface Msg {
  role: "user" | "assistant";
  text: string;
  evidence?: CopilotEvidence[];
  refused?: boolean;
}

// Fallbacks until the opener watchlist loads; then we specialise to the actual top centre.
const BASE_SUGGESTIONS = [
  "Which centres are drifting this period, and why?",
  "What is the project CPI this period?",
];

export function Copilot({ period = 12 }: { period?: number }) {
  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);
  const [top, setTop] = useState<WatchlistRow | null>(null);
  const [driftCount, setDriftCount] = useState<number | null>(null);

  // Proactive opener: learn the current drift watchlist so we can lead with a standing answer and
  // offer suggestions tied to the actual top centre (not generic strings).
  useEffect(() => {
    let cancelled = false;
    api.watchlist(Math.max(period, 4), 5)
      .then((d) => { if (!cancelled) { setTop(d.rows[0] ?? null); setDriftCount(d.rows.length); } })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [period]);

  const suggestions = [
    ...BASE_SUGGESTIONS,
    ...(top ? [`Explain the drift risk for ${top.bccId}.`, `Next-period spend forecast for ${top.bccId}?`] : []),
  ];

  async function send(question: string) {
    const q = question.trim();
    if (!q || busy) return;
    setInput("");
    const nextMsgs: Msg[] = [...msgs, { role: "user", text: q }];
    setMsgs(nextMsgs);
    setBusy(true);
    try {
      const history: CopilotTurn[] = nextMsgs.map((m) => ({ role: m.role, text: m.text }));
      const res = await api.askCopilot(q, history.slice(0, -1));
      setMsgs((m) => [...m, { role: "assistant", text: res.answer, evidence: res.evidence, refused: res.refused }]);
    } catch (e) {
      setMsgs((m) => [...m, { role: "assistant", text: `Error: ${String((e as Error).message)}`, refused: true }]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="copilot-layout">
      {/* ── chat is the hero: ad-hoc questions, every answer with its source trail ── */}
      <aside className="panel copilot">
        <div className="copilot-header">
          <div className="copilot-avatar" aria-hidden>✦</div>
          <div className="copilot-heading">
            <h2>QS Cost Copilot</h2>
            <p className="muted small">Ask in plain English — every number is read through tested tools and shows the rows behind it.</p>
          </div>
        </div>

        <div className="chat">
          {msgs.length === 0 && (
            <div className="chat-empty">
              <div className="chat-empty-label">Try asking</div>
              <div className="suggestions">
                {suggestions.map((s) => (
                  <button key={s} className="suggestion" onClick={() => send(s)}>
                    <span className="suggestion-arrow" aria-hidden>→</span>
                    <span>{s}</span>
                  </button>
                ))}
              </div>
            </div>
          )}
          {msgs.map((m, i) => (
            <div key={i} className={`msg ${m.role}`}>
              {m.role === "assistant" && <div className="msg-avatar" aria-hidden>✦</div>}
              <div className={`bubble ${m.role} ${m.refused ? "refused" : ""}`}>
                <div className="bubble-text">{m.text}</div>
                {m.evidence && m.evidence.length > 0 && <Sources evidence={m.evidence} />}
              </div>
            </div>
          ))}
          {busy && (
            <div className="msg assistant">
              <div className="msg-avatar" aria-hidden>✦</div>
              <div className="bubble assistant typing"><span /><span /><span /></div>
            </div>
          )}
        </div>

        <form className="composer" onSubmit={(e) => { e.preventDefault(); send(input); }}>
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="Ask about a centre, forecast, or the project CPI…"
            disabled={busy}
          />
          <button type="submit" disabled={busy || !input.trim()}>Ask</button>
        </form>
      </aside>

      {/* ── proactive drift watchlist: a secondary, collapsed standing answer ── */}
      <details className="card drift-collapsible">
        <summary className="drift-summary">
          <span className="pill pill-blue">DRIFT WATCHLIST</span>
          <span className="muted small">
            {driftCount != null ? `${driftCount} centres flagged this period` : "This period's standing answer"} — expand to view
          </span>
        </summary>
        <div className="drift-body">
          <Watchlist period={Math.max(period, 4)} k={5} />
        </div>
      </details>
    </div>
  );
}
