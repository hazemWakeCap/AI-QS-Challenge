import { useEffect, useState } from "react";
import { api, type ForecastListItem, type CentreForecast, type ProjectSpendScenario } from "../api/client";

const money = (v: number) => new Intl.NumberFormat(undefined, { maximumFractionDigits: 0 }).format(v);
const badgeClass = (t: string) => t === "Validatable" ? "tag-green" : t === "TooEarly" ? "tag-amber" : "tag-closed";

function ConeChart({ f }: { f: CentreForecast }) {
  const w = 540, h = 240, padL = 64, padR = 16, padT = 14, padB = 26;
  // cumulative P50 from increments (always available); band from the joint-sim cone (if available)
  const xs = [f.originPeriod, ...f.increments.map((b) => f.originPeriod + b.horizon)];
  let cum = f.acAtOrigin;
  const p50 = [f.acAtOrigin, ...f.increments.map((b) => (cum += b.p50))];
  const cone = f.cumulativeConeAvailable ? f.cumulativeCone : [];
  const lo = [f.acAtOrigin, ...cone.map((c) => c.p10 ?? NaN)];
  const hi = [f.acAtOrigin, ...cone.map((c) => c.p90 ?? NaN)];
  const bandOk = f.cumulativeConeAvailable && cone.length === f.increments.length;

  const allY = [f.bac, ...p50, ...(bandOk ? hi : []), f.acAtOrigin].filter((v) => Number.isFinite(v));
  const yMax = Math.max(...allY) * 1.08, yMin = 0;
  const x = (i: number) => padL + (i * (w - padL - padR)) / (xs.length - 1);
  const y = (v: number) => h - padB - ((v - yMin) / (yMax - yMin || 1)) * (h - padT - padB);

  const line = (arr: number[]) => arr.map((v, i) => `${x(i)},${y(v)}`).join(" ");
  const bandPts = bandOk
    ? [...hi.map((v, i) => `${x(i)},${y(v)}`), ...lo.map((v, i) => `${x(i)},${y(v)}`).reverse()].join(" ")
    : "";

  return (
    <svg width={w} height={h} className="cone">
      {/* BAC reference */}
      <line x1={padL} x2={w - padR} y1={y(f.bac)} y2={y(f.bac)} className="cone-bac" />
      <text x={padL} y={y(f.bac) - 4} className="cone-lbl">BAC {money(f.bac)}</text>
      {bandOk && <polygon points={bandPts} className="cone-band" />}
      <polyline points={line(p50)} className="cone-p50" fill="none" />
      {p50.map((v, i) => <circle key={i} cx={x(i)} cy={y(v)} r={2.5} className="cone-dot" />)}
      {xs.map((p, i) => <text key={p} x={x(i)} y={h - 8} className="cone-lbl" textAnchor="middle">P{p}</text>)}
    </svg>
  );
}

export function ForecastCone({ rev }: { rev: number }) {
  const [list, setList] = useState<ForecastListItem[] | null>(null);
  const [bcc, setBcc] = useState("");
  const [f, setF] = useState<CentreForecast | null>(null);
  const [roll, setRoll] = useState<ProjectSpendScenario | null>(null);
  const [showFinal, setShowFinal] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  useEffect(() => {
    setErr(null);
    api.forecastCostCentres().then((l) => { setList(l); setBcc((b) => b || l[0]?.bccId || ""); })
      .catch((e) => setErr(String(e.message ?? e)));
    api.forecastRollup().then(setRoll).catch(() => {});
  }, [rev]);

  useEffect(() => {
    if (!bcc) return;
    api.forecastCone(bcc).then(setF).catch((e) => setErr(String(e.message ?? e)));
  }, [bcc, rev]);

  if (err) return <div className="error">Forecast unavailable: {err}</div>;
  if (!list) return <div className="muted">Loading forecast…</div>;

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">COST CONE</span>
        <span className="muted small">next-period incremental spend · nominal 80% band</span>
        <select value={bcc} onChange={(e) => setBcc(e.target.value)} style={{ marginLeft: "auto" }}>
          {list.map((c) => <option key={c.bccId} value={c.bccId}>{c.bccId} · {c.trust}</option>)}
        </select>
      </div>

      {roll && (
        <div className="muted small" style={{ marginBottom: 10 }}>
          Project next-period spend scenario (h=1, {roll.centres} centres, MC {roll.draws}):
          <b> {money(roll.p10)}</b> · <b>{money(roll.p50)}</b> · <b>{money(roll.p90)}</b> (P10·P50·P90) —
          scenario spread, not a probability; assumes centre independence.
        </div>
      )}

      {f && (
        <>
          <div className="panel-head">
            <span className={`tag ${badgeClass(f.trust)}`}>{f.trust.replace(/([A-Z])/g, " $1").trim()}</span>
            <span className="muted small">origin P{f.originPeriod} · {f.progressPct.toFixed(0)}% complete · BAC {money(f.bac)} · AC {money(f.acAtOrigin)}</span>
          </div>
          <ConeChart f={f} />
          <table className="grid" style={{ marginTop: 10 }}>
            <thead><tr><th>Horizon</th><th className="num">P10</th><th className="num">P50 (incremental)</th><th className="num">P90</th></tr></thead>
            <tbody>
              {f.increments.map((b) => (
                <tr key={b.horizon}>
                  <td>+{b.horizon} period{b.horizon > 1 ? "s" : ""}</td>
                  <td className="num">{b.available && b.p10 != null ? money(b.p10) : "—"}</td>
                  <td className="num">{money(b.p50)}</td>
                  <td className="num">{b.available && b.p90 != null ? money(b.p90) : "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {!f.cumulativeConeAvailable && <div className="muted small">Cumulative band unavailable (insufficient joint calibration) — showing the P50 trajectory only.</div>}
          <div className="muted small" style={{ marginTop: 8 }}>
            <label><input type="checkbox" checked={showFinal} onChange={(e) => setShowFinal(e.target.checked)} /> show directional final-cost (not validated)</label>
            {showFinal && f.directionalFinalCost != null && <span> — ≈ {money(f.directionalFinalCost)} (BAC/CPI-style extrapolation; no final-cost ground truth on this project)</span>}
          </div>
        </>
      )}
    </div>
  );
}
