import { useEffect, useState } from "react";
import { api, type ValidationSummary, type ScorerReport } from "../api/client";
import { pctOfFraction as pct } from "../format";
import { Spinner } from "./Loading";

export function ValidationPanel() {
  const [data, setData] = useState<ValidationSummary | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    api.validationSummary().then(setData).catch((e) => setError(String(e.message ?? e)));
  }, []);

  if (error) return <div className="error">Validation summary unavailable: {error}</div>;
  if (!data) return <Spinner label="Loading model validation…" />;

  const at = (reports: ScorerReport[], k: number) => reports.find((r) => r.k === k);
  const rule5 = at(data.rule, 5);
  const rule10 = at(data.rule, 10);
  const bestCpi5 = data.cpiNative
    .filter((r) => r.k === 5)
    .reduce<ScorerReport | null>((b, r) => ((r.macroPrecision ?? 0) > (b?.macroPrecision ?? 0) ? r : b), null);

  return (
    <aside className="panel">
      <h2>Model validation</h2>
      <p className="badge-hist">Historical backtest — not this period's live accuracy</p>

      <div className="kpis">
        <Kpi label="precision@5" value={pct(rule5?.macroPrecision ?? null)}
             sub={`range ${pct(rule5?.precisionMin ?? null)}–${pct(rule5?.precisionMax ?? null)}`} />
        <Kpi label="precision@10" value={pct(rule10?.macroPrecision ?? null)} />
        <Kpi label="false alerts / cycle" value={(rule5?.falseAlertsPerCycle ?? 0).toFixed(1)} />
        <Kpi label="transitions" value={String(data.totalTransitions)} sub={`${data.foldCount} folds`} />
      </div>

      <h3>Rule vs CPI-native (precision@5)</h3>
      <table className="mini">
        <tbody>
          <tr className="hi">
            <td>Rule (deployed)</td>
            <td className="num">{pct(rule5?.macroPrecision ?? null)}</td>
          </tr>
          {data.cpiNative
            .filter((r) => r.k === 5)
            .map((r) => (
              <tr key={r.scorerLabel}>
                <td>{r.scorerLabel.replace("cpi-native:", "CPI · ")}</td>
                <td className="num">{pct(r.macroPrecision)}</td>
              </tr>
            ))}
        </tbody>
      </table>
      {rule5 && bestCpi5 && (
        <p className="muted small">
          The deployed transparent rule leads the best CPI-native baseline by{" "}
          {(((rule5.macroPrecision ?? 0) - (bestCpi5.macroPrecision ?? 0)) * 100).toFixed(0)}pp — reported
          descriptively (the rule ships regardless).
        </p>
      )}

      <p className="muted small">{data.provenance}</p>
      <p className="muted small">
        {data.scorerVersion} · {data.featureSchemaVersion} · origins {data.evaluationOriginMin}–
        {data.evaluationOriginMax}
      </p>
    </aside>
  );
}

function Kpi({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="kpi">
      <div className="kpi-v">{value}</div>
      <div className="kpi-l">{label}</div>
      {sub && <div className="kpi-sub">{sub}</div>}
    </div>
  );
}
