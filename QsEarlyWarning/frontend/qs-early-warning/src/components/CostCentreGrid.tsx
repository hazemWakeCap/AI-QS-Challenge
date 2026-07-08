import { useEffect, useState } from "react";
import { api, type CostCentreEvm } from "../api/client";
import { money, ratio, pct } from "../format";
import { Spinner } from "./Loading";

export function CostCentreGrid({ period, rev, currency = "AED" }: { period: number; rev: number; currency?: string }) {
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
  if (!rows) return <Spinner />;
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
                <td className="num">{money(r.bac, currency)}</td>
                <td className="num">{pct(r.plannedPct)}</td>
                <td className="num">{pct(r.actualPct)}</td>
                <td className="num">{money(r.ev, currency)}</td>
                <td className="num">{money(r.ac, currency)}</td>
                <td className={`num ${r.cpi != null && r.cpi < 0.95 ? "bad" : ""}`}>{ratio(r.cpi)}</td>
                <td className="num">{money(r.eac, currency)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
