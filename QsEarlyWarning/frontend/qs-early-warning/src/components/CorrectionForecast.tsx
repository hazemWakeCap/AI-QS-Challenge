import { type CentreForecast } from "../api/client";
import { money as fmtMoney } from "../format";

// Plain integer with thousands separators (currency stated once in the caption) — keeps the 6-column
// table inside the 560px drawer without wrapping "… AED" onto a second line.
const INT = new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 });
const num = (v: number) => INT.format(Math.round(v));

// One correction action, as estimated by the copilot. `overrunReductionPct` (0–1) is the model's
// best-guess fraction of the projected cost overrun this action removes once fully in effect.
export interface ParsedScenario {
  id: string;
  title: string;
  overrunReductionPct: number;
  rationale?: string;
  evidenceNeeded?: string;
}

// Fixed CVD-safe categorical order (validated via the dataviz palette script, light surface).
// Deliberately avoids the amber BAC line and the gray "no action" baseline.
const SCEN_COLORS = ["#2a78d6", "#1baf7a", "#4a3aa7"];
const MAX_LINES = 3;

// The action ramps in over the 3 months — it doesn't bite fully on day one.
const PHASE = [0.5, 0.85, 1.0];

interface Row {
  scenario: ParsedScenario | null; // null = "No action" baseline
  color: string;
  dashed: boolean;
  cum: number[]; // cumulative cost at [origin, +1, +2, +3]
  final: number; // cum at +3
  saving: number; // baseline.final − this.final (0 for baseline)
}

// Deterministic projection: baseline is the forecaster's own P50 cumulative; each scenario reduces the
// per-period incremental spend by its (phased) estimated overrun-reduction. All AED math is here in TS
// over the validated forecast increments; the AI supplies only the reduction fraction.
export function CorrectionForecast({
  baseline,
  scenarios,
  currency = "AED",
}: {
  baseline: CentreForecast;
  scenarios: ParsedScenario[];
  currency?: string;
}) {
  const money = (v: number) => fmtMoney(v, currency);
  const incs = [...baseline.increments].sort((a, b) => a.horizon - b.horizon).slice(0, 3);
  if (incs.length === 0) return null;

  const ac0 = baseline.acAtOrigin;
  const p50 = incs.map((b) => b.p50);
  const cumOf = (adj: number[]) => {
    const out = [ac0];
    let c = ac0;
    for (let i = 0; i < adj.length; i++) { c += adj[i]; out.push(c); }
    return out;
  };
  const baselineCum = cumOf(p50);
  const baselineFinal = baselineCum[baselineCum.length - 1];

  const scenarioRows: Row[] = scenarios.map((s) => {
    const r = Math.min(1, Math.max(0, s.overrunReductionPct));
    const adj = p50.map((v, i) => v * (1 - (PHASE[i] ?? 1) * r));
    const cum = cumOf(adj);
    const final = cum[cum.length - 1];
    return { scenario: s, color: "", dashed: false, cum, final, saving: baselineFinal - final };
  });
  // Keep the highest-impact actions on the chart (≤4 series total); the rest stay in the prose list.
  const shown = scenarioRows.sort((a, b) => b.saving - a.saving).slice(0, MAX_LINES)
    .map((row, i) => ({ ...row, color: SCEN_COLORS[i % SCEN_COLORS.length] }));

  const baselineRow: Row = { scenario: null, color: "var(--muted)", dashed: true, cum: baselineCum, final: baselineFinal, saving: 0 };
  const rows: Row[] = [baselineRow, ...shown];
  const hiddenCount = scenarios.length - shown.length;

  const o = baseline.originPeriod;
  const xLabels = [o, o + 1, o + 2, o + 3];

  // Frame the y-axis to the trajectory data (a 3-month horizon sits far below BAC, so anchoring at 0 or
  // at BAC would flatten the scenario fan). Draw the BAC reference only when it actually falls in range.
  const allCum = rows.flatMap((r) => r.cum);
  const dMin = Math.min(...allCum), dMax = Math.max(...allCum);
  const span = Math.max(1, dMax - dMin);
  const yMin = Math.max(0, dMin - 0.18 * span), yMax = dMax + 0.2 * span;
  const showBac = baseline.bac >= yMin && baseline.bac <= yMax;

  return (
    <div className="corr-forecast">
      <div className="panel-head" style={{ marginTop: 4 }}>
        <span className="pill pill-blue">3-MONTH WHAT-IF</span>
        <span className="muted small">projected cumulative cost · no action vs each correction</span>
      </div>

      <ScenarioChart rows={rows} bac={baseline.bac} xLabels={xLabels} yMin={yMin} yMax={yMax} showBac={showBac} />

      <div className="corr-legend">
        <span className="corr-key"><i className="corr-swatch dashed" style={{ background: "var(--muted)" }} />No action (baseline)</span>
        {shown.map((row) => (
          <span key={row.scenario!.id} className="corr-key">
            <i className="corr-swatch" style={{ background: row.color }} />{row.scenario!.title}
          </span>
        ))}
        {showBac && <span className="corr-key"><i className="corr-swatch dashed" style={{ background: "var(--warn)" }} />BAC {money(baseline.bac)}</span>}
      </div>

      <div className="grid-scroll" style={{ marginTop: 10 }}>
        <table className="grid">
          <thead>
            <tr>
              <th>Scenario</th>
              <th className="num">P{o + 1}</th>
              <th className="num">P{o + 2}</th>
              <th className="num">P{o + 3} (3-mo)</th>
              <th className="num">Δ vs no action</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((row, i) => (
              <tr key={row.scenario?.id ?? "baseline"} className={i === 0 ? "mini" : undefined}>
                <td>
                  <span className="corr-swatch" style={{ background: i === 0 ? "var(--muted)" : row.color }} />
                  {row.scenario ? row.scenario.title : "No action"}
                </td>
                <td className="num mono">{num(row.cum[1])}</td>
                <td className="num mono">{num(row.cum[2])}</td>
                <td className="num mono">{num(row.cum[3])}</td>
                <td className={`num mono ${row.saving > 0 ? "good" : ""}`}>{i === 0 ? "—" : `−${num(row.saving)}`}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted small" style={{ marginTop: 4 }}>Cumulative cost in {currency}.</p>

      <p className="muted small" style={{ marginTop: 8 }}>
        Illustrative — the <b>no-action baseline is the forecast model's P50</b> (origin P{o}, trust {baseline.trust});
        each correction's impact is an <b>AI estimate</b> of overrun recovered, phased in over 3 months. Not a validated
        commitment.{!showBac ? ` The 3-month spend stays below BAC ${money(baseline.bac)}.` : ""}
        {hiddenCount > 0 ? ` Showing the top ${shown.length} of ${scenarios.length} actions.` : ""}
      </p>
    </div>
  );
}

// Inline-SVG multi-line chart, modeled on ForecastCone's ConeChart (reuses .cone* classes for grid/labels).
function ScenarioChart({ rows, bac, xLabels, yMin, yMax, showBac }:
  { rows: Row[]; bac: number; xLabels: number[]; yMin: number; yMax: number; showBac: boolean }) {
  const w = 500, h = 220, padL = 60, padR = 44, padT = 14, padB = 24;
  const n = xLabels.length; // 4 points (origin + 3)
  const x = (i: number) => padL + (i * (w - padL - padR)) / (n - 1);
  const y = (v: number) => h - padB - ((v - yMin) / (yMax - yMin || 1)) * (h - padT - padB);
  const line = (arr: number[]) => arr.map((v, i) => `${x(i)},${y(v)}`).join(" ");
  const gridlines = [0, 0.25, 0.5, 0.75, 1].map((f) => yMin + (yMax - yMin) * f);
  const axis = (v: number) => v >= 1e6 ? `${(v / 1e6).toFixed(2)}M` : v >= 1e3 ? `${Math.round(v / 1e3)}k` : `${Math.round(v)}`;

  return (
    <svg width={w} height={h} className="cone" viewBox={`0 0 ${w} ${h}`} role="img" aria-label="3-month correction forecast">
      {gridlines.map((v, i) => (
        <g key={i}>
          <line x1={padL} x2={w - padR} y1={y(v)} y2={y(v)} className="cone-grid" />
          <text x={padL - 6} y={y(v) + 3} className="cone-lbl" textAnchor="end">{axis(v)}</text>
        </g>
      ))}
      {/* BAC reference — only when it falls inside the framed range */}
      {showBac && <line x1={padL} x2={w - padR} y1={y(bac)} y2={y(bac)} className="cone-bac" />}
      {/* one polyline per scenario/baseline */}
      {rows.map((r, i) => (
        <g key={i}>
          <polyline points={line(r.cum)} fill="none" stroke={r.color} strokeWidth={2}
                    strokeDasharray={r.dashed ? "5 4" : undefined} opacity={r.dashed ? 0.9 : 1} />
          {r.cum.map((v, j) => <circle key={j} cx={x(j)} cy={y(v)} r={2.3} fill={r.color} />)}
          {/* endpoint value label */}
          <text x={x(n - 1) + 4} y={y(r.cum[n - 1]) + 3} className="cone-lbl" style={{ fill: r.dashed ? "var(--muted)" : r.color }}>
            {axis(r.cum[n - 1])}
          </text>
        </g>
      ))}
      {xLabels.map((p, i) => <text key={p} x={x(i)} y={h - 7} className="cone-lbl" textAnchor="middle">P{p}</text>)}
    </svg>
  );
}
