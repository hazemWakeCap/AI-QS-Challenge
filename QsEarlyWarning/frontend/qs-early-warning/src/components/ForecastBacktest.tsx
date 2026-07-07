import { useEffect, useState } from "react";
import { api, type ForecastBacktest, type HorizonMetric } from "../api/client";

const PREDICTORS = ["model", "planned-spend", "cpi-based", "recent-run-rate", "zero-increment"];

export function ForecastBacktestPanel({ rev }: { rev: number }) {
  const [d, setD] = useState<ForecastBacktest | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [band, setBand] = useState<"overall" | "early">("early");

  useEffect(() => {
    setErr(null);
    api.forecastBacktest().then(setD).catch((e) => setErr(String(e.message ?? e)));
  }, [rev]);

  if (err) return <div className="error">{err}</div>;
  if (!d) return <div className="muted">Loading back-test…</div>;

  const rows = band === "overall" ? d.overall : d.earlyBand;
  const horizons = [1, 2, 3];
  const cell = (p: string, h: number) => rows.find((m) => m.predictor === p && m.horizon === h);
  const best = (h: number) => Math.min(...rows.filter((m) => m.horizon === h).map((m) => m.maePctOfBac));
  const modelCov = (h: number): HorizonMetric | undefined => cell("model", h);

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">BACK-TEST</span>
        <span className="muted small">grouped rolling-origin · MAE as % of BAC · lower is better</span>
        <select value={band} onChange={(e) => setBand(e.target.value as "overall" | "early")} style={{ marginLeft: "auto" }}>
          <option value="early">early band (&lt;40% progress)</option>
          <option value="overall">overall</option>
        </select>
      </div>
      <div className="muted small">folds {d.foldsEvaluated} evaluated / {d.foldsSkipped} skipped · origins {d.originMin}–{d.originMax}</div>

      <table className="grid" style={{ marginTop: 10 }}>
        <thead><tr><th>Predictor</th>{horizons.map((h) => <th key={h} className="num">h={h}</th>)}</tr></thead>
        <tbody>
          {PREDICTORS.map((p) => (
            <tr key={p}>
              <td className={p === "model" ? "mono" : "muted"}>{p === "model" ? <b>model</b> : p}</td>
              {horizons.map((h) => {
                const m = cell(p, h);
                const isBest = m && Math.abs(m.maePctOfBac - best(h)) < 1e-9;
                return <td key={h} className="num" style={isBest ? { color: "var(--forecast)", fontWeight: 700 } : undefined}>{m ? m.maePctOfBac.toFixed(2) + "%" : "—"}</td>;
              })}
            </tr>
          ))}
        </tbody>
      </table>

      <h3>Measured coverage (nominal 80% interval)</h3>
      <table className="grid">
        <thead><tr><th>Horizon</th><th className="num">coverage</th><th className="num">n</th><th>Wilson 95%</th></tr></thead>
        <tbody>
          {horizons.map((h) => { const m = modelCov(h); return (
            <tr key={h}>
              <td>h={h}</td>
              <td className="num">{m?.coverage != null ? (m.coverage * 100).toFixed(0) + "%" : "—"}</td>
              <td className="num">{m?.n ?? "—"}</td>
              <td className="muted">{m?.coverageLow != null && m?.coverageHigh != null ? `${(m.coverageLow * 100).toFixed(0)}–${(m.coverageHigh * 100).toFixed(0)}%` : "—"}</td>
            </tr>
          ); })}
        </tbody>
      </table>

      <p className="muted small">Coverage is <b>measured, not asserted</b> — the interval is a nominal 80% band; the achieved fraction below 80% reflects temporal drift (calibration strictly earlier than the evaluated period). Model and baselines are scored on identical eligible rows.</p>
      {d.notes.map((n, i) => <p key={i} className="muted small">{n}</p>)}
    </div>
  );
}
