import { useState } from "react";
import { api, type CopilotTurn, type CopilotEvidence } from "../api/client";

interface Msg {
  role: "user" | "assistant";
  text: string;
  evidence?: CopilotEvidence[];
  refused?: boolean;
}

const SUGGESTIONS = [
  "Which centres are about to tip to AMBER in period 12, and why?",
  "Explain the drift risk for the top centre.",
  "How accurate is the model?",
];

export function Copilot() {
  const [msgs, setMsgs] = useState<Msg[]>([]);
  const [input, setInput] = useState("");
  const [busy, setBusy] = useState(false);

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
      setMsgs((m) => [
        ...m,
        { role: "assistant", text: res.answer, evidence: res.evidence, refused: res.refused },
      ]);
    } catch (e) {
      setMsgs((m) => [...m, { role: "assistant", text: `Error: ${String((e as Error).message)}`, refused: true }]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <aside className="panel copilot">
      <h2>QS Cost Copilot</h2>
      <p className="muted small">Ask about the watchlist in plain English. Answers are grounded in the same read-only tools.</p>

      <div className="chat">
        {msgs.length === 0 && (
          <div className="suggestions">
            {SUGGESTIONS.map((s) => (
              <button key={s} className="suggestion" onClick={() => send(s)}>
                {s}
              </button>
            ))}
          </div>
        )}
        {msgs.map((m, i) => (
          <div key={i} className={`bubble ${m.role} ${m.refused ? "refused" : ""}`}>
            <div className="bubble-text">{m.text}</div>
            {m.evidence && m.evidence.length > 0 && (
              <div className="evidence">
                {m.evidence.map((e, j) => (
                  <span key={j} className="chip" title={e.detail}>
                    {e.tool}
                    {e.detail ? ` (${e.detail})` : ""}
                  </span>
                ))}
              </div>
            )}
          </div>
        ))}
        {busy && <div className="bubble assistant muted">Thinking…</div>}
      </div>

      <form
        className="composer"
        onSubmit={(e) => {
          e.preventDefault();
          send(input);
        }}
      >
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Ask about a centre or the watchlist…"
          disabled={busy}
        />
        <button type="submit" disabled={busy || !input.trim()}>
          Ask
        </button>
      </form>
    </aside>
  );
}
