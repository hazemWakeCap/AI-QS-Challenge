import { useEffect, useState } from "react";
import { api, session, type Health, type Project } from "./api/client";
import { EvmOverview } from "./components/EvmOverview";
import { CostCentreGrid } from "./components/CostCentreGrid";
import { CapturePanel } from "./components/CapturePanel";
import { PeriodsPanel } from "./components/PeriodsPanel";
import { Watchlist } from "./components/Watchlist";
import { ValidationPanel } from "./components/ValidationPanel";
import { Copilot } from "./components/Copilot";
import { DataAdmin } from "./components/DataAdmin";
import { ForecastCone } from "./components/ForecastCone";
import { ForecastBacktestPanel } from "./components/ForecastBacktest";

type Tab = "overview" | "centres" | "capture" | "workflow" | "data" | "watchlist" | "forecast" | "insight";
const TABS: { id: Tab; label: string }[] = [
  { id: "overview", label: "EVM Overview" },
  { id: "centres", label: "Cost Centres" },
  { id: "capture", label: "Monthly Capture" },
  { id: "workflow", label: "Periods & Estimate" },
  { id: "data", label: "Data Admin" },
  { id: "watchlist", label: "Watchlist" },
  { id: "forecast", label: "Forecast" },
  { id: "insight", label: "Model & Copilot" },
];

export default function App() {
  const [health, setHealth] = useState<Health | null>(null);
  const [projects, setProjects] = useState<Project[]>([]);
  const [slug, setSlug] = useState<string>("");
  const [range, setRange] = useState<{ min: number; forecast: number }>({ min: 1, forecast: 12 });
  const [period, setPeriod] = useState(12);
  const [tab, setTab] = useState<Tab>("overview");
  const [rev, setRev] = useState(0);
  const [err, setErr] = useState<string | null>(null);

  const project = projects.find((p) => p.slug === slug) ?? null;

  useEffect(() => {
    api.health().then(setHealth).catch(() => {});
    api.projects().then((ps) => {
      setProjects(ps);
      if (ps[0]) { session.projectSlug = ps[0].slug; setSlug(ps[0].slug); }  // set before children fetch
    }).catch((e) => setErr(String(e.message ?? e)));
  }, []);

  // When the selected project changes: point the client at it, learn its period range.
  useEffect(() => {
    if (!slug) return;
    session.projectSlug = slug;
    setErr(null);
    api.overview().then((o) => {
      setRange({ min: o.minPeriod, forecast: o.forecastPeriod });
      setPeriod(o.forecastPeriod);
      setRev((r) => r + 1);
    }).catch((e) => setErr(String(e.message ?? e)));
  }, [slug]);

  const periods = Array.from({ length: range.forecast - range.min + 1 }, (_, i) => range.min + i);
  const refresh = () => setRev((r) => r + 1);

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>QS Cost — System of Record</h1>
          <p className="muted">Live EVM from Postgres · multi-project · data entry replaces the spreadsheet.</p>
        </div>
        {health && <div className="health muted small">scorer {health.scorerVersion} · {health.centreCount} centres loaded</div>}
      </header>

      <div className="controls">
        <label>Project&nbsp;
          <select value={slug} onChange={(e) => { session.projectSlug = e.target.value; setSlug(e.target.value); }}>
            {projects.map((p) => <option key={p.slug} value={p.slug}>{p.name} ({p.reportingCurrency})</option>)}
          </select>
        </label>
        <label>Period&nbsp;
          <select value={period} onChange={(e) => setPeriod(Number(e.target.value))}>
            {periods.map((p) => <option key={p} value={p}>Period {p}{p === range.forecast ? " — forecast" : ""}</option>)}
          </select>
        </label>
        {project && <span className="muted small">{project.ledgerActive ? "ledger active" : "cumulative snapshot"} · active estimate v{project.activeEstimateVersionId ?? "—"}</span>}
      </div>

      {err && <div className="error">{err}</div>}

      <nav className="tabs">
        {TABS.map((t) => (
          <button key={t.id} className={tab === t.id ? "tab active" : "tab"} onClick={() => setTab(t.id)}>{t.label}</button>
        ))}
      </nav>

      <div className="content">
        {!slug ? <div className="muted">No project available for this user.</div> : (
          <>
            {tab === "overview" && <section className="card"><EvmOverview period={period} rev={rev} /></section>}
            {tab === "centres" && <section className="card"><CostCentreGrid period={period} rev={rev} /></section>}
            {tab === "capture" && <section className="card narrow"><CapturePanel period={period} rev={rev} onChanged={refresh} /></section>}
            {tab === "workflow" && <section className="card narrow"><PeriodsPanel rev={rev} onChanged={refresh} activeVersionId={project?.activeEstimateVersionId ?? null} /></section>}
            {tab === "data" && <section className="card"><DataAdmin rev={rev} onChanged={refresh} /></section>}
            {tab === "watchlist" && <section className="card"><Watchlist period={Math.max(period, 4)} k={10} /></section>}
            {tab === "forecast" && (
              <div className="split">
                <section className="card"><ForecastCone rev={rev} /></section>
                <section className="card"><ForecastBacktestPanel rev={rev} /></section>
              </div>
            )}
            {tab === "insight" && (
              <div className="split">
                <section className="card"><ValidationPanel /></section>
                <section className="card"><Copilot /></section>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
