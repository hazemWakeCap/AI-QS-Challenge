import { useEffect, useState } from "react";
import { api, type Period } from "../api/client";

export function PeriodsPanel({ rev, onChanged, activeVersionId }: { rev: number; onChanged: () => void; activeVersionId: number | null }) {
  const [periods, setPeriods] = useState<Period[]>([]);
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let off = false;
    api.periods().then((x) => !off && setPeriods(x)).catch((e) => !off && setMsg({ ok: false, text: String(e.message ?? e) }));
    return () => { off = true; };
  }, [rev]);

  async function act(fn: () => Promise<unknown>, label: string) {
    setBusy(true); setMsg(null);
    try { await fn(); setMsg({ ok: true, text: `${label} ✓` }); onChanged(); }
    catch (e: unknown) { setMsg({ ok: false, text: String((e as Error).message ?? e) }); }
    finally { setBusy(false); }
  }

  return (
    <div>
      <div className="panel-head"><span className="pill pill-blue">REPORTING WORKFLOW</span><span className="muted small">open / close periods · publish estimate</span></div>
      <div className="workflow-actions">
        <button disabled={busy || activeVersionId == null}
          onClick={() => act(() => api.publishVersion(activeVersionId!), `published estimate v${activeVersionId}`)}>
          Publish active estimate (v{activeVersionId ?? "—"})
        </button>
      </div>
      <table className="grid">
        <thead><tr><th>Period</th><th>Start</th><th>Status</th><th>Actions</th></tr></thead>
        <tbody>
          {periods.map((p) => (
            <tr key={p.id}>
              <td>Period {p.period}</td>
              <td className="muted">{p.periodStart.slice(0, 7)}</td>
              <td><span className={`tag tag-${p.status === "closed" ? "closed" : "green"}`}>{p.status}</span></td>
              <td>
                {p.status === "open"
                  ? <button className="btn btn-sm btn-secondary" disabled={busy} onClick={() => act(() => api.closePeriod(p.period), `closed period ${p.period}`)}>Close</button>
                  : <button className="btn btn-sm btn-secondary" disabled={busy} onClick={() => act(() => api.openPeriod(p.period), `re-opened period ${p.period}`)}>Re-open</button>}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {msg && <div className={msg.ok ? "ok-msg" : "error"}>{msg.text}</div>}
    </div>
  );
}
