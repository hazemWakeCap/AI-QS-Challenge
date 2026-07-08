import { useEffect, useState } from "react";
import { api, type WatchlistResponse } from "../api/client";
import { Spinner } from "./Loading";

export function Watchlist({ period, k, onSelect }: { period: number; k: number; onSelect?: (bccId: string) => void }) {
  const [data, setData] = useState<WatchlistResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    api
      .watchlist(period, k)
      .then((d) => !cancelled && setData(d))
      .catch((e) => !cancelled && setError(String(e.message ?? e)))
      .finally(() => !cancelled && setLoading(false));
    return () => {
      cancelled = true;
    };
  }, [period, k]);

  if (loading && !data) return <Spinner label="Loading watchlist…" />;
  if (error) return <div className="error">Could not load period {period}: {error}</div>;
  if (!data) return null;

  return (
    <div>
      <div className="watchlist-head">
        <span className={`pill ${data.isForecast ? "pill-forecast" : "pill-hist"}`}>
          {data.isForecast ? "LIVE FORECAST" : "HISTORICAL (out-of-fold)"}
        </span>
        <span className="muted">
          {data.eligibleCount} GREEN centres eligible · model cutoff {data.trainingCutoffPeriod} ·{" "}
          {data.artifactVersion}
        </span>
      </div>

      <table className="grid">
        <thead>
          <tr>
            <th style={{ width: 40 }}>#</th>
            <th>Cost Centre</th>
            <th>Discipline</th>
            <th className="num">Risk</th>
            <th className="num">CPI</th>
            <th className="num">Gap (pp)</th>
            <th>Why flagged</th>
          </tr>
        </thead>
        <tbody>
          {data.rows.map((r) => (
            <tr key={r.bccId} className={onSelect ? "clickable" : undefined}
                onClick={onSelect ? () => onSelect(r.bccId) : undefined}
                title={onSelect ? "Explain this centre's variance" : undefined}>
              <td className="rank">{r.rank}</td>
              <td className="mono">{r.bccId}</td>
              <td>{r.discipline ?? "—"}</td>
              <td className="num">
                <RiskBar value={r.riskScore} />
              </td>
              <td className="num mono">{r.cpi.toFixed(3)}</td>
              <td className="num mono">{r.gap.toFixed(1)}</td>
              <td className="indicators">
                {r.riskIndicators.map((i, idx) => (
                  <span key={idx} className="chip">
                    {i}
                  </span>
                ))}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function RiskBar({ value }: { value: number }) {
  const pct = Math.round(Math.min(1, Math.max(0, value)) * 100);
  const hue = 120 - Math.round(value * 120); // green→red
  return (
    <div className="riskbar" title={value.toFixed(3)}>
      <div className="riskbar-fill" style={{ width: `${pct}%`, background: `hsl(${hue} 70% 45%)` }} />
      <span className="riskbar-label">{value.toFixed(2)}</span>
    </div>
  );
}
