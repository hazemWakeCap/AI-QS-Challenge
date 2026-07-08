import { useEffect, useState } from "react";
import { api, type CopilotTurn, type CopilotEvidence, type WatchlistRow } from "../api/client";
import { Watchlist } from "./Watchlist";

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
      {/* ── proactive drift-watchlist opener (answered without being asked) ── */}
      <section className="card">
        <div className="panel-head">
          <span className="pill pill-blue">DRIFT WATCHLIST</span>
          <span className="muted small">
            Opened on the standing answer{driftCount != null ? ` · ${driftCount} centres flagged this period` : ""}. Ask a follow-up below.
          </span>
        </div>
        <Watchlist period={Math.max(period, 4)} k={5} />
      </section>

      {/* ── chat: ad-hoc questions, every answer with its source trail ── */}
      <aside className="panel copilot">
        <h2>QS Cost Copilot</h2>
        <p className="muted small">Ask in plain English. Every number is read through tested tools and shows the rows behind it.</p>

        <div className="chat">
          {msgs.length === 0 && (
            <div className="suggestions">
              {suggestions.map((s) => (
                <button key={s} className="suggestion" onClick={() => send(s)}>{s}</button>
              ))}
            </div>
          )}
          {msgs.map((m, i) => (
            <div key={i} className={`bubble ${m.role} ${m.refused ? "refused" : ""}`}>
              <div className="bubble-text">{m.text}</div>
              {m.evidence && m.evidence.length > 0 && <Sources evidence={m.evidence} />}
            </div>
          ))}
          {busy && <div className="bubble assistant muted">Thinking…</div>}
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
    </div>
  );
}

// The "sources" panel: for every tool call, the resolved filter/period, excluded-row count, and the
// source row keys — so a wrong-period or wrong-grain answer is caught in the citation.
function Sources({ evidence }: { evidence: CopilotEvidence[] }) {
  return (
    <details className="sources">
      <summary className="muted small">sources · {evidence.length} tool call{evidence.length === 1 ? "" : "s"}</summary>
      <ul className="sources-list">
        {evidence.map((e, j) => (
          <li key={j}>
            <span className="chip mono">{e.tool}</span>
            {e.sources?.filter && <span className="muted small"> {e.sources.filter}</span>}
            {e.sources?.excludedCount != null && e.sources.excludedCount > 0 && (
              <span className="muted small"> · {e.sources.excludedCount} rows excluded</span>
            )}
            {e.sources && e.sources.rowIds.length > 0 && (
              <div className="source-rows">
                {e.sources.sheet && <span className="muted small">{e.sources.sheet}: </span>}
                <span className="mono small">{e.sources.rowIds.slice(0, 12).join(", ")}{e.sources.rowIds.length > 12 ? ` +${e.sources.rowIds.length - 12}` : ""}</span>
              </div>
            )}
          </li>
        ))}
      </ul>
    </details>
  );
}
