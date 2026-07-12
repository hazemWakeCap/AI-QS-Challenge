import { useEffect, useState } from "react";
import { api, type CostCentreEvm } from "../api/client";
import { money, ratio, pct } from "../format";
import { Spinner } from "./Loading";

export function CostCentreGrid({ period, rev, currency = "AED", onSelect, selectedBcc }: { period: number; rev: number; currency?: string; onSelect?: (row: CostCentreEvm) => void; selectedBcc?: string | null }) {
  const [rows, setRows] = useState<CostCentreEvm[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [q, setQ] = useState("");
  const [status, setStatus] = useState("");
  const [sort, setSort] = useState<{ key: "cpi" | "spi"; dir: 1 | -1 } | null>(null);

  const toggleSort = (key: "cpi" | "spi") =>
    setSort((s) => (s?.key === key ? { key, dir: s.dir === 1 ? -1 : 1 } : { key, dir: 1 }));
  const arrow = (key: "cpi" | "spi") => (sort?.key === key ? (sort.dir === 1 ? " ▲" : " ▼") : "");

  useEffect(() => {
    let off = false;
    setErr(null);
    api.costCentres(period).then((x) => !off && setRows(x)).catch((e) => !off && setErr(String(e.message ?? e)));
    return () => { off = true; };
  }, [period, rev]);

  if (err) return <div className="error">{err}</div>;
  if (!rows) return <Spinner />;
  const statuses = [...new Set(rows.map((r) => r.alertLevel))];
  const filtered = rows.filter(
    (r) =>
      (!q || r.bccId.toLowerCase().includes(q.toLowerCase())) &&
      (!status || r.alertLevel === status)
  );
  const sorted = sort
    ? [...filtered].sort((a, b) => {
        const av = a[sort.key], bv = b[sort.key];
        if (av == null) return 1; // nulls (not-started / no ratio) sink to bottom
        if (bv == null) return -1;
        return (av - bv) * sort.dir;
      })
    : filtered;

  return (
    <div>
      <div className="panel-head">
        <span className="muted small">{sorted.length} cost centres · period {period}</span>
        <select className="search" value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All statuses</option>
          {statuses.map((s) => <option key={s} value={s}>{s.replace("_", " ")}</option>)}
        </select>
        <input className="search" placeholder="filter BCC…" value={q} onChange={(e) => setQ(e.target.value)} />
      </div>
      <div className="grid-scroll">
        <table className="grid">
          <thead>
            <tr><th>Cost Centre</th><th>Discipline</th><th>Status</th><th className="num">BAC</th><th className="num">Plan%</th><th className="num">Act%</th><th className="num">EV</th><th className="num">AC</th><th className="num sortable" onClick={() => toggleSort("cpi")} title="Sort by CPI">CPI{arrow("cpi")}</th><th className="num sortable" onClick={() => toggleSort("spi")} title="Sort by SPI">SPI{arrow("spi")}</th><th className="num">EAC</th></tr>
          </thead>
          <tbody>
            {sorted.map((r) => (
              <tr key={r.bccId}
                  className={[onSelect && "clickable", r.bccId === selectedBcc && "selected"].filter(Boolean).join(" ") || undefined}
                  onClick={onSelect ? () => onSelect(r) : undefined}
                  title={onSelect ? "Open cost-centre details" : undefined}>
                <td className="mono">{r.bccId}</td>
                <td className="muted">{r.discipline ?? "—"}</td>
                <td><span className={`tag tag-${r.alertLevel.toLowerCase().replace("_", "")}`}>{r.alertLevel.replace("_", " ")}</span></td>
                <td className="num">{money(r.bac, currency)}</td>
                <td className="num">{pct(r.plannedPct)}</td>
                <td className="num">{pct(r.actualPct)}</td>
                <td className="num">{money(r.ev, currency)}</td>
                <td className="num">{money(r.ac, currency)}</td>
                <td className={`num ${r.cpi != null && r.cpi < 0.95 ? "bad" : ""}`}>{ratio(r.cpi)}</td>
                <td className={`num ${r.spi != null && r.spi < 0.95 ? "bad" : ""}`}>{ratio(r.spi)}</td>
                <td className="num">{money(r.eac, currency)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
