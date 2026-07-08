import { useEffect, useState } from "react";
import { api, type EvmOverview as Overview } from "../api/client";
import { money, ratio, pct } from "../format";
import { Spinner } from "./Loading";

function Spark({ points, pick, hi }: { points: Overview["trend"]; pick: (p: Overview["trend"][0]) => number | null; hi: number }) {
  const vals = points.map(pick);
  const nums = vals.filter((v): v is number => v != null);
  if (nums.length < 2) return null;
  const min = Math.min(...nums, hi), max = Math.max(...nums, hi);
  const w = 260, h = 44, pad = 4;
  const x = (i: number) => pad + (i * (w - 2 * pad)) / (points.length - 1);
  const y = (v: number) => h - pad - ((v - min) / (max - min || 1)) * (h - 2 * pad);
  const pts = vals.map((v, i) => (v == null ? null : [x(i), y(v)] as [number, number])).filter((p): p is [number, number] => p != null);
  const line = pts.map((p) => `${p[0]},${p[1]}`).join(" ");
  const area = `${pts[0][0]},${h - pad} ${line} ${pts[pts.length - 1][0]},${h - pad}`;
  return (
    <svg width={w} height={h} className="spark">
      <defs>
        <linearGradient id="sparkGrad" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor="var(--accent)" stopOpacity="0.35" />
          <stop offset="100%" stopColor="var(--accent)" stopOpacity="0" />
        </linearGradient>
      </defs>
      <polygon points={area} className="spark-fill" />
      <line x1={pad} x2={w - pad} y1={y(hi)} y2={y(hi)} className="spark-th" />
      <polyline points={line} className="spark-line" />
    </svg>
  );
}

export function EvmOverview({ period, rev, currency = "AED" }: { period: number; rev: number; currency?: string }) {
  const [d, setD] = useState<Overview | null>(null);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    let off = false;
    setErr(null);
    api.overview(period).then((x) => !off && setD(x)).catch((e) => !off && setErr(String(e.message ?? e)));
    return () => { off = true; };
  }, [period, rev]);

  if (err) return <div className="error">Overview failed: {err}</div>;
  if (!d) return <Spinner label="Loading EVM…" />;
  const t = d.totals;
  const cur = t.currency || currency;
  const over = t.cpi != null && t.cpi < 1;

  return (
    <div>
      <div className="panel-head">
        <span className={`pill ${over ? "pill-amber" : "pill-green"}`}>PROJECT EVM</span>
        <span className="muted small">period {d.period} · {cur} · {t.costCentres} centres · {t.amber} AMBER</span>
      </div>
      <div className="kpis">
        <div className="kpi"><div className="kpi-v">{money(t.bac, cur)}</div><div className="kpi-l">BAC (budget)</div></div>
        <div className="kpi"><div className="kpi-v">{money(t.ev, cur)}</div><div className="kpi-l">EV (earned)</div></div>
        <div className="kpi"><div className="kpi-v">{money(t.ac, cur)}</div><div className="kpi-l">AC (actual)</div></div>
        <div className={`kpi ${over ? "bad" : "good"}`}><div className="kpi-v">{ratio(t.cpi)}</div><div className="kpi-l">CPI</div></div>
        <div className="kpi"><div className="kpi-v">{ratio(t.spi)}</div><div className="kpi-l">SPI</div></div>
        <div className="kpi"><div className="kpi-v">{money(t.eac, cur)}</div><div className="kpi-l">EAC (forecast)</div></div>
        <div className={`kpi ${t.vac < 0 ? "bad" : "good"}`}><div className="kpi-v">{money(t.vac, cur)}</div><div className="kpi-l">VAC</div></div>
        <div className="kpi"><div className="kpi-v">{pct(t.pctBudgetConsumed)}</div><div className="kpi-l">budget consumed</div></div>
      </div>
      <div className="trend">
        <div className="trend-item">
          <div className="muted small">CPI trend (target 1.00)</div>
          <Spark points={d.trend} pick={(p) => p.cpi} hi={1} />
        </div>
        <div className="trend-item">
          <div className="muted small">SPI trend (target 1.00)</div>
          <Spark points={d.trend} pick={(p) => p.spi} hi={1} />
        </div>
      </div>
    </div>
  );
}
