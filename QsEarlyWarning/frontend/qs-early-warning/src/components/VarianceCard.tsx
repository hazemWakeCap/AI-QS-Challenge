import { useEffect, useState } from "react";
import { api, type VarianceBridge } from "../api/client";
import { money as fmtMoney, ratio } from "../format";
import { Spinner } from "./Loading";

// Idea-5 variance-attribution card: the drill-down behind idea-1's watchlist. Two honest lanes (CV by
// resource, monetary SV), a tie-out, and honesty markers (assumption badge + evidence-needed). CV is an
// ATTRIBUTION, not a proven cause.
export function VarianceCard({ bcc, period, currency = "AED" }: { bcc: string; period: number; currency?: string }) {
  const [d, setD] = useState<VarianceBridge | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const money = (v: number | null | undefined) => fmtMoney(v, currency);

  useEffect(() => {
    setD(null); setErr(null);
    api.variance(bcc, period).then(setD).catch((e) => setErr(String(e.message ?? e)));
  }, [bcc, period]);

  if (err) return <div className="error">{err}</div>;
  if (!d) return <Spinner label={`Loading variance for ${bcc}…`} />;

  if (!d.available) {
    return (
      <div>
        <div className="panel-head"><span className="pill pill-blue">VARIANCE</span><span className="mono">{bcc}</span></div>
        <p className="muted">{d.unavailableReason ?? "Not diagnosable."}</p>
      </div>
    );
  }

  const over = (d.cvAed ?? 0) < 0;
  const dom = d.contributions.find((c) => c.resourceType === d.dominantResourceType);
  const verb = over ? "Over" : "Under";

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">VARIANCE</span>
        <span className="mono">{bcc}</span>
        <span className="muted small">{d.package} · {d.discipline ?? "—"} · period {d.periodId}</span>
      </div>

      {/* fact-first attribution line (cause = hypothesis) */}
      <div className={over ? "error" : "ok-msg"} style={{ marginBottom: 12 }}>
        <b>{verb} by {money(Math.abs(d.cvAed ?? 0))}</b> (CV). Schedule {Math.abs(d.svAed ?? 0) < 1 ? "on-plan" : (d.svAed! < 0 ? "behind" : "ahead")} (SV {money(d.svAed)}
        {d.spi != null ? `, SPI ${ratio(d.spi)}` : ""}).{" "}
        {d.resourceBreakdownAvailable && d.dominantResourceType
          ? (d.dominantResourceType === "unexplained residual"
              ? <><b>Unexplained residual</b> dominates — the recorded AC splits don't sum to total AC.</>
              : <><b>{d.dominantResourceType.toLowerCase()}</b> is the dominant cost-variance contributor{dom?.timesNormBudget != null ? ` at ~${dom.timesNormBudget.toFixed(2)}× its norm-implied budget` : ""}.</>)
          : <span className="muted">Resource breakdown unavailable (no estimate for this project) — CV/SV totals only.</span>}
      </div>

      {/* honesty markers */}
      {d.assumptionBased && (
        <div className="varflags">
          <span className="tag tag-amber">assumption-based attribution</span>
          {d.evidenceNeeded && <span className="muted small">evidence to confirm cause: <b>{d.evidenceNeeded}</b></span>}
        </div>
      )}

      <div className="kpis" style={{ marginTop: 12 }}>
        <div className="kpi"><div className="kpi-v">{money(d.pv)}</div><div className="kpi-l">PV (planned)</div></div>
        <div className="kpi"><div className="kpi-v">{money(d.ev)}</div><div className="kpi-l">EV (earned)</div></div>
        <div className="kpi"><div className="kpi-v">{money(d.ac)}</div><div className="kpi-l">AC (actual)</div></div>
        <div className={`kpi ${over ? "bad" : "good"}`}><div className="kpi-v">{money(d.cvAed)}</div><div className="kpi-l">CV = EV − AC</div></div>
      </div>

      {d.resourceBreakdownAvailable && <Waterfall d={d} />}

      {/* CV-by-resource table */}
      {d.resourceBreakdownAvailable && (
        <table className="grid" style={{ marginTop: 12 }}>
          <thead><tr><th>Resource</th><th className="num">EV_r (norm budget)</th><th className="num">AC_r (actual)</th><th className="num">CV_r</th><th className="num">×norm</th></tr></thead>
          <tbody>
            {d.contributions.map((c) => (
              <tr key={c.resourceType} className={c.resourceType === d.dominantResourceType ? "mini hi" : undefined}>
                <td>{c.resourceType}</td>
                <td className="num mono">{money(c.evR)}</td>
                <td className="num mono">{money(c.acR)}</td>
                <td className={`num mono ${c.cvR < 0 ? "bad" : ""}`}>{money(c.cvR)}</td>
                <td className="num mono">{c.timesNormBudget != null ? c.timesNormBudget.toFixed(2) + "×" : "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <p className="muted small" style={{ marginTop: 10 }}>
        Tie-out: {d.tiesOut ? `✓ Σ CVr + unexplained residual = CV, to the ${currency}` : "⚠ does not tie out"}
        {d.unexplainedResidual != null ? ` · unexplained residual ${money(d.unexplainedResidual)} (AC the four splits don't attribute)` : ""}.
      </p>
      {d.notes.map((n, i) => <p key={i} className="muted small">{n}</p>)}
    </div>
  );
}

// Inline-SVG waterfall: PV → +SV → EV → per-resource legs (AC_r − EV_r) → residual → AC. An overrun leg
// moves cost UP (red); a saving moves it down (green). Dominant resource outlined.
function Waterfall({ d }: { d: VarianceBridge }) {
  const pv = d.pv ?? 0, ev = d.ev ?? 0, ac = d.ac ?? 0;
  const steps: { label: string; delta: number; kind: "base" | "up" | "down" | "total"; hi?: boolean }[] = [];
  steps.push({ label: "PV", delta: pv, kind: "base" });
  steps.push({ label: "SV", delta: ev - pv, kind: ev - pv >= 0 ? "up" : "down" });
  for (const c of d.contributions) {
    const leg = c.acR - c.evR; // EV→AC direction: positive = spent more than norm budget (cost up)
    if (Math.abs(leg) < 0.5) continue;
    steps.push({ label: c.resourceType.slice(0, 4), delta: leg, kind: leg >= 0 ? "up" : "down", hi: c.resourceType === d.dominantResourceType });
  }
  const residual = ac - d.contributions.reduce((s, c) => s + c.acR, 0);
  if (Math.abs(residual) >= 0.5) steps.push({ label: "resid", delta: residual, kind: residual >= 0 ? "up" : "down" });
  steps.push({ label: "AC", delta: ac, kind: "total" });

  const W = 520, H = 164, pad = 24, topPad = 16, bw = Math.min(56, (W - 2 * pad) / steps.length - 8);
  const maxV = Math.max(pv, ev, ac, 1) * 1.1;
  const y = (v: number) => H - pad - (v / maxV) * (H - pad - topPad);
  const compact = (v: number) => { const a = Math.abs(v); const s = v < 0 ? "−" : ""; return a >= 1e6 ? `${s}${(a / 1e6).toFixed(1)}M` : a >= 1e3 ? `${s}${Math.round(a / 1e3)}k` : `${s}${Math.round(a)}`; };
  let cum = 0;
  const bars = steps.map((s, i) => {
    const x = pad + i * ((W - 2 * pad) / steps.length);
    let top: number, bot: number;
    if (s.kind === "base" || s.kind === "total") { top = y(s.delta); bot = y(0); cum = s.delta; }
    else { const start = cum; cum += s.delta; top = y(Math.max(start, cum)); bot = y(Math.min(start, cum)); }
    const fill = s.kind === "base" ? "var(--muted)" : s.kind === "total" ? "var(--accent)" : s.kind === "up" ? "var(--bad)" : "var(--good)";
    return { x, top, h: Math.max(2, bot - top), fill, label: s.label, val: compact(s.delta), hi: s.hi };
  });

  return (
    <svg className="waterfall" viewBox={`0 0 ${W} ${H}`} width="100%" role="img" aria-label="variance waterfall">
      {bars.map((b, i) => (
        <g key={i}>
          <rect x={b.x} y={b.top} width={bw} height={b.h} fill={b.fill}
                stroke={b.hi ? "var(--text)" : "none"} strokeWidth={b.hi ? 2 : 0} rx={2} />
          <text x={b.x + bw / 2} y={b.top - 4} className="cone-lbl" textAnchor="middle">{b.val}</text>
          <text x={b.x + bw / 2} y={H - pad + 12} className="cone-lbl" textAnchor="middle">{b.label}</text>
        </g>
      ))}
    </svg>
  );
}
