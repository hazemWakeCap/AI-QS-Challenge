import { useEffect, useState } from "react";
import { api, type CostCentreEvm } from "../api/client";

const money = (v: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 }).format(v);

export function CostCentreGrid({ period, rev }: { period: number; rev: number }) {
  const [rows, setRows] = useState<CostCentreEvm[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [q, setQ] = useState("");

  useEffect(() => {
    let off = false;
    setErr(null);
    api.costCentres(period).then((x) => !off && setRows(x)).catch((e) => !off && setErr(String(e.message ?? e)));
    return () => { off = true; };
  }, [period, rev]);

  if (err) return <div className="error">{err}</div>;
  if (!rows) return <div className="muted">Loading…</div>;
  const filtered = rows.filter((r) => !q || r.bccId.toLowerCase().includes(q.toLowerCase()));

  return (
    <div>
      <div className="panel-head">
        <span className="muted small">{filtered.length} cost centres · period {period}</span>
        <input className="search" placeholder="filter BCC…" value={q} onChange={(e) => setQ(e.target.value)} />
      </div>
      <div className="grid-scroll">
        <table className="grid">
          <thead>
            <tr><th>Cost Centre</th><th>Discipline</th><th>Status</th><th className="num">BAC</th><th className="num">Plan%</th><th className="num">Act%</th><th className="num">EV</th><th className="num">AC</th><th className="num">CPI</th><th className="num">EAC</th></tr>
          </thead>
          <tbody>
            {filtered.map((r) => (
              <tr key={r.bccId}>
                <td className="mono">{r.bccId}</td>
                <td className="muted">{r.discipline ?? "—"}</td>
                <td><span className={`tag tag-${r.alertLevel.toLowerCase().replace("_", "")}`}>{r.alertLevel.replace("_", " ")}</span></td>
                <td className="num">{money(r.bac)}</td>
                <td className="num">{r.plannedPct?.toFixed(1) ?? "—"}</td>
                <td className="num">{r.actualPct?.toFixed(1) ?? "—"}</td>
                <td className="num">{money(r.ev)}</td>
                <td className="num">{money(r.ac)}</td>
                <td className={`num ${r.cpi != null && r.cpi < 0.95 ? "bad" : ""}`}>{r.cpi?.toFixed(3) ?? "—"}</td>
                <td className="num">{money(r.eac)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
