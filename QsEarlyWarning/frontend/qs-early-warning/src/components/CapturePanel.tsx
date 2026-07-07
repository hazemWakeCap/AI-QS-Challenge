import { useEffect, useState } from "react";
import { api, type CostCentreEvm } from "../api/client";

export function CapturePanel({ period, rev, onChanged }: { period: number; rev: number; onChanged: () => void }) {
  const [centres, setCentres] = useState<CostCentreEvm[]>([]);
  const [bcc, setBcc] = useState("");
  const [pct, setPct] = useState("");
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let off = false;
    api.costCentres(period).then((x) => {
      if (off) return;
      setCentres(x);
      setBcc((b) => b || x[0]?.bccId || "");
    }).catch(() => {});
    return () => { off = true; };
  }, [period, rev]);

  const current = centres.find((c) => c.bccId === bcc);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setBusy(true); setMsg(null);
    try {
      await api.captureProgress(bcc, period, Number(pct));
      setMsg({ ok: true, text: `Captured ${pct}% for ${bcc} at period ${period}.` });
      setPct("");
      onChanged();
    } catch (err: unknown) {
      setMsg({ ok: false, text: String((err as Error).message ?? err) });
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="panel-head"><span className="pill pill-blue">MONTHLY CAPTURE</span><span className="muted small">enter actual % complete · period {period}</span></div>
      <p className="muted small">This replaces the spreadsheet: the value is written to Postgres, EVM recomputes, and the watchlist/overview refresh. Closed periods are rejected.</p>
      <form className="capture" onSubmit={submit}>
        <label>Cost centre
          <select value={bcc} onChange={(e) => setBcc(e.target.value)}>
            {centres.map((c) => <option key={c.bccId} value={c.bccId}>{c.bccId}</option>)}
          </select>
        </label>
        {current && <div className="muted small">current: plan {current.plannedPct?.toFixed(1) ?? "—"}% · actual {current.actualPct?.toFixed(1) ?? "—"}% · CPI {current.cpi?.toFixed(3) ?? "—"}</div>}
        <label>Actual % complete
          <input type="number" min={0} max={100} step="0.1" value={pct} onChange={(e) => setPct(e.target.value)} required />
        </label>
        <button disabled={busy || !bcc || pct === ""}>{busy ? "Saving…" : "Capture"}</button>
      </form>
      {msg && <div className={msg.ok ? "ok-msg" : "error"}>{msg.text}</div>}
    </div>
  );
}
