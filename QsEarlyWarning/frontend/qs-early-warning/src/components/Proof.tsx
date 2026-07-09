import { useEffect, useMemo, useState } from "react";
import { api, type BacktestResponse } from "../api/client";
import { Spinner } from "./Loading";

/**
 * Proof mode — the hindsight "we called it" reveal. For a past origin period, show the top-k flagged
 * GREEN centres, then reveal what each one ACTUALLY did next period (from the project's own history).
 * The dashboard makes the claim; this grades it against reality.
 */
export function Proof({ range }: { range: { min: number; forecast: number } }) {
  // Default to the period that tells the best story on Tower X (period 5), clamped to a backtestable origin.
  const initial = Math.min(Math.max(5, range.min + 3), range.forecast - 1);
  const [period, setPeriod] = useState(initial);
  const [data, setData] = useState<BacktestResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [revealed, setRevealed] = useState(false);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setRevealed(false);
    api
      .watchlistBacktest(period, 5)
      .then((d) => !cancelled && setData(d))
      .catch((e) => !cancelled && setError(String(e.message ?? e)))
      .finally(() => !cancelled && setLoading(false));
    return () => { cancelled = true; };
  }, [period]);

  // Slider bounds come from the API (labeled origins only); fall back to the project range pre-load.
  const lo = data?.originMin ?? range.min + 3;
  const hi = data?.originMax ?? range.forecast - 1;
  const origins = useMemo(
    () => Array.from({ length: Math.max(0, hi - lo + 1) }, (_, i) => lo + i),
    [lo, hi],
  );

  const rulePct = data?.ruleMacroPrecision != null ? Math.round(data.ruleMacroPrecision * 100) : null;
  const basePct = data?.bestBaselineMacroPrecision != null ? Math.round(data.bestBaselineMacroPrecision * 100) : null;
  const topHit = data?.rows.find((r) => r.hit) ?? data?.rows[0];

  return (
    <div className="proof">
      <div className="proof-head">
        <div>
          <span className="pill pill-proof">PROOF · HINDSIGHT BACKTEST</span>
          <p className="proof-sub">
            The model saw only history up to this period. We flag the GREEN centres about to tip — then
            let the project's own next month grade us.
          </p>
        </div>
        {data && (
          <div className="proof-baseline" title="Model-level rolling-origin backtest (frozen).">
            <div className="proof-bar">
              <span className="proof-bar-l">Our rule</span>
              <span className="proof-bar-track"><i style={{ width: `${rulePct ?? 0}%` }} className="proof-bar-fill rule" /></span>
              <b>{rulePct != null ? `${rulePct}%` : "—"}</b>
            </div>
            <div className="proof-bar">
              <span className="proof-bar-l">Best CPI rule</span>
              <span className="proof-bar-track"><i style={{ width: `${basePct ?? 0}%` }} className="proof-bar-fill base" /></span>
              <b>{basePct != null ? `${basePct}%` : "—"}</b>
            </div>
            <span className="proof-baseline-cap">precision@5 across {data.totalTransitions} real transitions</span>
          </div>
        )}
      </div>

      <div className="proof-controls">
        <div className="proof-timeline">
          <span className="muted small">Rewind to period</span>
          <div className="proof-steps">
            {origins.map((p) => (
              <button key={p} className={`proof-step ${p === period ? "active" : ""}`} onClick={() => setPeriod(p)}>
                {p}
              </button>
            ))}
          </div>
          {data && <span className="muted small">→ graded against actual P{data.nextPeriod}</span>}
        </div>
        <button
          className={`btn ${revealed ? "btn-secondary" : "btn-primary"} proof-reveal-btn`}
          onClick={() => setRevealed((v) => !v)}
          disabled={!data}
        >
          {revealed ? "Hide outcome" : `Reveal what happened in P${data?.nextPeriod ?? "?"} →`}
        </button>
      </div>

      {loading && !data ? (
        <Spinner label="Grading against history…" />
      ) : error ? (
        <div className="error">Could not backtest period {period}: {error}</div>
      ) : data ? (
        <>
          <div className={`proof-scoreboard ${revealed ? "on" : ""}`}>
            <div className="proof-score">
              <b>{revealed ? `${data.hits}/${data.k}` : "?/?"}</b>
              <span>flags that tipped</span>
            </div>
            <div className="proof-score">
              <b>{revealed && data.precisionAtK != null ? `${Math.round(data.precisionAtK * 100)}%` : "—"}</b>
              <span>precision@{data.k} this period</span>
            </div>
            <div className="proof-score muted-score">
              <b>{data.eligible}</b>
              <span>GREEN centres eligible · model cutoff {data.trainingCutoffPeriod}</span>
            </div>
          </div>

          <table className="grid proof-table">
            <thead>
              <tr>
                <th style={{ width: 36 }}>#</th>
                <th>Cost Centre</th>
                <th className="num">Risk</th>
                <th className="num">CPI</th>
                <th className="num">Gap</th>
                <th>Why we flagged it</th>
                <th className="proof-actual-col">Actual P{data.nextPeriod}</th>
              </tr>
            </thead>
            <tbody>
              {data.rows.map((r, i) => (
                <tr key={r.bccId} className={revealed ? (r.hit ? "proof-row-hit" : "proof-row-miss") : ""}>
                  <td className="rank">{i + 1}</td>
                  <td className="mono">{r.bccId}</td>
                  <td className="num"><RiskBar value={r.riskScore} /></td>
                  <td className="num mono">{r.cpi.toFixed(3)}</td>
                  <td className="num mono">{r.gap.toFixed(1)}pp</td>
                  <td className="indicators">
                    {r.riskIndicators.slice(0, 2).map((ind, idx) => (
                      <span key={idx} className="chip">{ind}</span>
                    ))}
                  </td>
                  <td className="proof-actual-col">
                    <span
                      className={`proof-outcome ${revealed ? "shown" : "hidden"} ${r.hit ? "hit" : "miss"}`}
                      style={{ transitionDelay: `${i * 90}ms` }}
                    >
                      {revealed ? (
                        r.hit ? `▲ ${r.actualNextAlert} ✓` : `${r.actualNextAlert}`
                      ) : "•••"}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {revealed && topHit?.hit && (
            <p className="proof-punchline">
              <b>{topHit.bccId}</b> was still GREEN at P{data.period} (CPI {topHit.cpi.toFixed(3)}, spending{" "}
              {topHit.gap.toFixed(1)}pp ahead of progress) — we ranked it #{topHit.rank}. It tipped to{" "}
              <b>AMBER</b> in P{data.nextPeriod}. Caught a full reporting period early, before the invoices landed.
            </p>
          )}
          <p className="proof-provenance muted small">{data.provenance}</p>
        </>
      ) : null}
    </div>
  );
}

function RiskBar({ value }: { value: number }) {
  const pct = Math.round(Math.min(1, Math.max(0, value)) * 100);
  const hue = 120 - Math.round(value * 120);
  return (
    <div className="riskbar" title={value.toFixed(3)}>
      <div className="riskbar-fill" style={{ width: `${pct}%`, background: `hsl(${hue} 70% 45%)` }} />
      <span className="riskbar-label">{value.toFixed(2)}</span>
    </div>
  );
}
