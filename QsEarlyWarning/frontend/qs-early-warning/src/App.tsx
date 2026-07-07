import { useEffect, useState } from "react";
import { api, type Health } from "./api/client";
import { Watchlist } from "./components/Watchlist";
import { ValidationPanel } from "./components/ValidationPanel";
import { Copilot } from "./components/Copilot";

// Retrospective origins 4..11 + the live forecast period 12.
const PERIODS = [4, 5, 6, 7, 8, 9, 10, 11, 12];

export default function App() {
  const [period, setPeriod] = useState(12);
  const [k, setK] = useState(10);
  const [health, setHealth] = useState<Health | null>(null);

  useEffect(() => {
    api.health().then(setHealth).catch(() => setHealth(null));
  }, []);

  return (
    <div className="app">
      <header className="topbar">
        <div>
          <h1>QS Cost Early-Warning</h1>
          <p className="muted">
            GREEN cost centres about to tip AMBER — one reporting period early.
          </p>
        </div>
        {health && (
          <div className="health muted small">
            {health.centreCount} centres · {health.rowCount} rows · {health.workbook}
          </div>
        )}
      </header>

      <div className="controls">
        <label>
          Reporting period&nbsp;
          <select value={period} onChange={(e) => setPeriod(Number(e.target.value))}>
            {PERIODS.map((p) => (
              <option key={p} value={p}>
                Period {p}
                {p === 12 ? " — live forecast" : ""}
              </option>
            ))}
          </select>
        </label>
        <label>
          Show top&nbsp;
          <select value={k} onChange={(e) => setK(Number(e.target.value))}>
            <option value={5}>5</option>
            <option value={10}>10</option>
          </select>
        </label>
      </div>

      <div className="layout">
        <main className="main">
          <Watchlist period={period} k={k} />
        </main>
        <div className="sidebar">
          <ValidationPanel />
          <Copilot />
        </div>
      </div>
    </div>
  );
}
