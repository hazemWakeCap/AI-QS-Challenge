import { useCallback, useEffect, useState } from "react";
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
import { StressTest } from "./components/StressTest";
import { VarianceCard } from "./components/VarianceCard";
import { ProjectsAdmin } from "./components/ProjectsAdmin";
import { EmptyState } from "./components/Loading";

type Tab = "overview" | "centres" | "capture" | "workflow" | "data" | "watchlist" | "forecast" | "stress" | "insight" | "projects";
const TABS: { id: Tab; label: string }[] = [
  { id: "overview", label: "EVM Overview" },
  { id: "centres", label: "Cost Centres" },
  { id: "capture", label: "Monthly Capture" },
  { id: "workflow", label: "Periods & Estimate" },
  { id: "data", label: "Data Admin" },
  { id: "watchlist", label: "Watchlist" },
  { id: "forecast", label: "Forecast" },
  { id: "stress", label: "Stress Test" },
  { id: "insight", label: "Model & Copilot" },
  { id: "projects", label: "Projects" },
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
  const [varianceBcc, setVarianceBcc] = useState<string | null>(null);

  const project = projects.find((p) => p.slug === slug) ?? null;
  // A project with no published estimate has no EVM data yet — its read endpoints would error, so we
  // show an empty state and skip the data fetches until a workbook is imported.
  const isEmpty = !!project && project.activeEstimateVersionId == null;
  const cur = project?.reportingCurrency ?? "AED";

  // Load (or reload) the project list; keep the current selection if it still exists, else fall back to
  // the first project (or none). Passed to ProjectsAdmin so create/delete/rename refresh the switcher.
  const loadProjects = useCallback(async () => {
    try {
      const ps = await api.projects();
      setProjects(ps);
      setSlug((cur) => {
        const next = cur && ps.some((p) => p.slug === cur) ? cur : (ps[0]?.slug ?? "");
        session.projectSlug = next;
        return next;
      });
    } catch (e: unknown) { setErr(String((e as Error).message ?? e)); }
  }, []);

  useEffect(() => {
    api.health().then(setHealth).catch(() => {});
    loadProjects();
  }, [loadProjects]);

  // When the selected project changes: point the client at it, learn its period range.
  useEffect(() => {
    if (!slug) return;
    session.projectSlug = slug;
    setErr(null);
    if (isEmpty) { setRange({ min: 1, forecast: 12 }); setRev((r) => r + 1); return; }  // no data → skip overview
    api.overview().then((o) => {
      setRange({ min: o.minPeriod, forecast: o.forecastPeriod });
      setPeriod(o.forecastPeriod);
      setRev((r) => r + 1);
    }).catch((e) => setErr(String(e.message ?? e)));
  }, [slug, isEmpty]);

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
            {projects.length === 0 && <option value="">— none —</option>}
            {projects.map((p) => <option key={p.slug} value={p.slug}>{p.name} ({p.reportingCurrency})</option>)}
          </select>
        </label>
        <button className="btn btn-sm btn-secondary" onClick={() => setTab("projects")}>+ New / Manage</button>
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
        {tab === "projects" ? (
          <section className="card"><ProjectsAdmin projects={projects} onProjectsChanged={loadProjects} /></section>
        ) : !slug ? (
          <section className="card"><EmptyState icon="◇" title="No project yet"
            hint="Open the Projects tab to create one or import a workbook." /></section>
        ) : isEmpty && tab !== "data" ? (
          <section className="card"><EmptyState icon="◷" title={`“${project?.name}” has no data yet`}
            hint="Open Projects to import a workbook, or use Data Admin to add rows manually." /></section>
        ) : (
          <>
            {tab === "overview" && <section className="card"><EvmOverview period={period} rev={rev} currency={cur} /></section>}
            {tab === "centres" && <section className="card"><CostCentreGrid period={period} rev={rev} currency={cur} /></section>}
            {tab === "capture" && <section className="card narrow"><CapturePanel period={period} rev={rev} onChanged={refresh} /></section>}
            {tab === "workflow" && <section className="card narrow"><PeriodsPanel rev={rev} onChanged={refresh} activeVersionId={project?.activeEstimateVersionId ?? null} /></section>}
            {tab === "data" && <section className="card"><DataAdmin rev={rev} onChanged={refresh} /></section>}
            {tab === "watchlist" && (
              <div className="content-stack">
                <section className="card">
                  <div className="panel-head"><span className="pill pill-blue">WATCHLIST</span>
                    <span className="muted small">Click a row to explain its variance (idea-5 attribution bridge).</span></div>
                  <Watchlist period={Math.max(period, 4)} k={10} onSelect={setVarianceBcc} />
                </section>
                {varianceBcc && <section className="card"><VarianceCard bcc={varianceBcc} period={Math.max(period, 4)} currency={cur} /></section>}
              </div>
            )}
            {tab === "forecast" && (
              <div className="split">
                <section className="card"><ForecastCone rev={rev} currency={cur} /></section>
                <section className="card"><ForecastBacktestPanel rev={rev} /></section>
              </div>
            )}
            {tab === "stress" && <section className="card"><StressTest rev={rev} /></section>}
            {tab === "insight" && (
              <div className="content-stack">
                <Copilot period={period} />
                <section className="card"><ValidationPanel /></section>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
