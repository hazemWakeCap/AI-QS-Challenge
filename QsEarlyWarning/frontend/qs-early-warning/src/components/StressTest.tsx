import { useEffect, useState } from "react";
import {
  api, type Reconciliation, type Assumptions, type PeerBenchmarkResponse, type AssumptionFlag,
} from "../api/client";

const AED = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });
const money = (v: number) => AED.format(Math.round(v)) + " AED";
const millions = (v: number) => (v / 1e6).toFixed(1) + "M";

// Class-2 flag kind → short label + tone.
const KIND: Record<string, { label: string; tone: string }> = {
  OutputNormTopPercentile: { label: "Output Norm ≥ P90", tone: "amber" },
  UnitRateBottomOfBand: { label: "Unit rate ≤ P10", tone: "amber" },
  ThinContingency: { label: "Thin contingency", tone: "amber" },
  ZeroContingency: { label: "Zero contingency", tone: "red" },
};

export function StressTest({ rev }: { rev: number }) {
  const [recon, setRecon] = useState<Reconciliation | null>(null);
  const [assume, setAssume] = useState<Assumptions | null>(null);
  const [peers, setPeers] = useState<PeerBenchmarkResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [kind, setKind] = useState<string>("all");

  useEffect(() => {
    setErr(null);
    Promise.all([api.stressReconciliation(), api.stressAssumptions(), api.stressPeerBenchmark()])
      .then(([r, a, p]) => { setRecon(r); setAssume(a); setPeers(p); })
      .catch((e) => setErr(String(e.message ?? e)));
  }, [rev]);

  if (err) return <div className="error">{err}</div>;
  if (!recon || !assume || !peers) return <div className="muted">Loading stress test…</div>;

  if (!recon.available) {
    return (
      <div>
        <div className="panel-head"><span className="pill pill-blue">STRESS TEST</span></div>
        <p className="muted">No estimate workbook for this project — the Estimate Assumption Stress Test runs
          only on the estimate's owning project (Tower&nbsp;X).</p>
      </div>
    );
  }

  const flags = kind === "all" ? assume.flags : assume.flags.filter((f) => f.kind === kind);
  const kinds = Array.from(new Set(assume.flags.map((f) => f.kind)));

  return (
    <div>
      <div className="panel-head">
        <span className="pill pill-blue">STRESS TEST</span>
        <span className="muted small">Estimate Assumption Stress Test · deterministic · run at award ·
          three separated output classes</span>
      </div>

      {/* ── Class 1: reconciliation tie-out (correctness proof, not a signal) ── */}
      <h3>Class 1 — Reconciliation tie-out</h3>
      {recon.tiesOut ? (
        <div className="ok-msg">
          ✓ Should-cost rebuilt from norms × rates ties out to the AED across all {recon.itemsChecked} BOQ
          items. The residual is exactly margin + contingency — this is a <b>correctness proof</b>, not a signal.
        </div>
      ) : (
        <div className="error">
          ✗ Reconciliation FAILED on {recon.itemsFailed}/{recon.itemsChecked} items
          (project direct Δ {money(recon.projectDirectDelta)}, uplift Δ {money(recon.projectUpliftDelta)}).
        </div>
      )}
      <div className="kpis" style={{ marginTop: 12 }}>
        <div className="kpi"><div className="kpi-v">{recon.itemsChecked}</div><div className="kpi-l">Items reconciled</div></div>
        <div className="kpi"><div className="kpi-v">{millions(recon.totalContractAmt)}</div><div className="kpi-l">Contract total</div></div>
        <div className="kpi"><div className="kpi-v">{millions(recon.totalMargin)}</div><div className="kpi-l">Margin</div></div>
        <div className="kpi"><div className="kpi-v">{millions(recon.totalContingency)}</div><div className="kpi-l">Contingency</div></div>
      </div>
      {!recon.tiesOut && recon.failedItems.length > 0 && (
        <table className="grid" style={{ marginTop: 10 }}>
          <thead><tr><th>Item</th><th>Failed check</th><th className="num">actual</th><th className="num">expected</th><th className="num">Δ</th></tr></thead>
          <tbody>
            {recon.failedItems.flatMap((it) => it.failures.map((f, i) => (
              <tr key={it.scope + f.check + i}>
                <td className="mono">{f.scope}{f.line ? ` · ${f.line}` : ""}</td>
                <td>{f.check}</td>
                <td className="num">{AED.format(Math.round(f.actual))}</td>
                <td className="num">{AED.format(Math.round(f.expected))}</td>
                <td className="num bad">{AED.format(Math.round(f.delta))}</td>
              </tr>
            )))}
          </tbody>
        </table>
      )}

      {/* ── Class 2: estimate-side assumption flags (day-zero, zero actuals) ── */}
      <h3>Class 2 — Unusual estimate assumptions ({assume.flags.length})</h3>
      <p className="muted small">Estimate-side review prompts (read no actuals): aggressive Output Norm,
        thin Unit Rate, thin/zero contingency. Cohort-gated (≥5), rules {assume.flags[0]?.rulesVersion ?? "v1"}.</p>

      {assume.heat.length > 0 ? (
        <div className="heat">
          {assume.heat.map((h) => (
            <div key={h.package} className={`heat-cell heat-${h.severity}`} title={`${h.package} · ${h.flagCount} flag(s)`}>
              <div className="heat-pkg">{h.package}</div>
              <div className="heat-meta">{h.discipline ?? "—"} · {h.flagCount}{h.highCount > 0 ? ` (${h.highCount} high)` : ""}</div>
            </div>
          ))}
        </div>
      ) : <p className="muted small">No estimate-side assumptions crossed the flag thresholds.</p>}

      {assume.flags.length > 0 && (
        <>
          <div className="controls" style={{ margin: "14px 0 8px" }}>
            <label>Kind&nbsp;
              <select value={kind} onChange={(e) => setKind(e.target.value)}>
                <option value="all">all ({assume.flags.length})</option>
                {kinds.map((k) => <option key={k} value={k}>{KIND[k]?.label ?? k} ({assume.flags.filter((f) => f.kind === k).length})</option>)}
              </select>
            </label>
          </div>
          <table className="grid">
            <thead><tr><th>Flag</th><th>Package</th><th>Discipline</th><th>Driving line</th><th>Reason</th></tr></thead>
            <tbody>
              {flags.map((f: AssumptionFlag, i) => (
                <tr key={f.package + f.kind + f.drivingResourceLine + i}>
                  <td><span className={`tag ${KIND[f.kind]?.tone === "red" ? "tag-amber" : "tag-green"}`}>{KIND[f.kind]?.label ?? f.kind}</span></td>
                  <td className="mono">{f.package}</td>
                  <td className="muted">{f.discipline ?? "—"}</td>
                  <td className="mono small">{f.drivingResourceLine ?? "—"}</td>
                  <td className="small">{f.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      {/* ── Class 3: retrospective peer benchmark (NOT an at-award flag) ── */}
      <h3>Class 3 — Peer benchmark <span className="pill pill-amber" style={{ marginLeft: 6 }}>RETROSPECTIVE</span></h3>
      <p className="muted small">Not an at-award flag: same-project peers do not exist at award. Package-cell
        grain (unit + resource type + procurement route), leave-one-out, suppressed below 5 distinct peer packages.</p>
      {peers.class3NoCellMeetsMinPeers ? (
        <div className="badge-hist">No cell meets the 5-peer minimum on this single-project workbook — every
          cell below shows its actual peer count (1–4), never falsely "0". A real day-zero benchmark needs
          completed prior-project peers.</div>
      ) : null}
      {peers.benchmarks.length > 0 ? (
        <table className="grid">
          <thead><tr><th>Package</th><th>Unit</th><th>Resource</th><th>Route</th><th className="num">est. unit cost</th><th className="num">peer median</th><th className="num">peers</th><th>status</th></tr></thead>
          <tbody>
            {peers.benchmarks.slice(0, 60).map((b, i) => (
              <tr key={b.package + b.unit + b.resourceType + i}>
                <td className="mono">{b.package}</td>
                <td>{b.unit ?? "—"}</td>
                <td className="muted">{b.resourceType ?? "—"}</td>
                <td className="muted small">{b.procurementRoute ?? "—"}</td>
                <td className="num">{money(b.estimatedUnitCost)}</td>
                <td className="num">{b.peerMedian != null ? money(b.peerMedian) : "—"}</td>
                <td className="num">{b.peerCount}</td>
                <td><span className={`tag ${b.status === "Benchmarked" ? "tag-green" : "tag-closed"}`}>{b.status}</span></td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : <p className="muted small">No estimate cells to benchmark.</p>}
      {peers.benchmarks.length > 60 && <p className="muted small">Showing 60 of {peers.benchmarks.length} cells.</p>}
    </div>
  );
}
