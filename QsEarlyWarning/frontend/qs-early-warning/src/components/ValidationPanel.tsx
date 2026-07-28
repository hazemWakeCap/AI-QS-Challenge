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
  const challengers = (data.challenger ?? []).filter((r) => r.k === 5);
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

      {data.collinearity?.zoneIsProxyForDiscipline && (
        <>
          <h3>The question we could not ask</h3>
          <p className="note-warn">
            We set out to test whether <b>physical neighbourhood</b> predicts drift. On this project
            it cannot be tested: {data.collinearity.singleDisciplineZones} of{" "}
            {data.collinearity.zoneCount} zones hold a single discipline, and{" "}
            <b>none of the {data.collinearity.disciplineCount} disciplines spans more than one zone</b>.
            A zone-neighbour feature therefore measures <b>trade</b>, not space — so we tested the two
            separately.
          </p>
        </>
      )}

      {challengers.length > 0 && rule5 && (
        <>
          <h3>Do a centre&apos;s peers predict its drift? (precision@5)</h3>
          <table className="mini">
            <tbody>
              <tr className="hi">
                <td>Rule (deployed)</td>
                <td className="num">{pct(rule5.macroPrecision)}</td>
                <td className="num muted">—</td>
              </tr>
              {challengers.map((r) => {
                const delta = (r.macroPrecision ?? 0) - (rule5.macroPrecision ?? 0);
                return (
                  <tr key={r.scorerLabel}>
                    <td>{PEER_LABELS[r.scorerLabel] ?? r.scorerLabel}</td>
                    <td className="num">{pct(r.macroPrecision)}</td>
                    <td className={`num ${delta > 0 ? "good" : delta < 0 ? "bad" : "muted"}`}>
                      {delta === 0 ? "—" : `${delta > 0 ? "+" : ""}${(delta * 100).toFixed(1)}pp`}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          <p className="muted small">
            Peer CPI is the leave-one-out ΣEV/ΣAC of a centre&apos;s neighbours, blended at a{" "}
            <b>predeclared</b> weight — not one fitted on these folds, which would let a challenger win
            by construction. Both run on the same {data.foldCount} folds and rank the same centres.
            {data.decisionsPerScorer ? (
              <>
                {" "}
                Each figure rests on {data.decisionsPerScorer} ranked slots, so read the per-fold spread
                below rather than the mean alone.
              </>
            ) : null}
          </p>
          <p className="muted small">
            Descriptive only — <b>{data.scorerVersion} remains deployed</b>. A challenger is a candidate,
            not a promotion.
          </p>

          <table className="mini">
            <thead>
              <tr>
                <th>Fold</th>
                <th className="num">Rule</th>
                {challengers.map((r) => (
                  <th key={r.scorerLabel} className="num">
                    {PEER_SHORT[r.scorerLabel] ?? r.scorerLabel}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rule5.folds.map((f, i) => (
                <tr key={f.periodId}>
                  <td>P{f.periodId}</td>
                  <td className="num">{pct(f.precision)}</td>
                  {challengers.map((r) => {
                    const cf = r.folds[i];
                    const better = (cf?.precision ?? 0) > (f.precision ?? 0);
                    const worse = (cf?.precision ?? 0) < (f.precision ?? 0);
                    return (
                      <td key={r.scorerLabel} className={`num ${better ? "good" : worse ? "bad" : ""}`}>
                        {pct(cf?.precision ?? null)}
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}

      <p className="muted small">{data.provenance}</p>
      <p className="muted small">
        {data.scorerVersion} · {data.featureSchemaVersion} · origins {data.evaluationOriginMin}–
        {data.evaluationOriginMax}
      </p>
    </aside>
  );
}

/** Named for what each challenger actually measures, never for what we hoped it measured. */
const PEER_LABELS: Record<string, string> = {
  "peer:peer-trade": "Peers · same trade",
  "peer:peer-spatial": "Peers · same place, different trade",
};
const PEER_SHORT: Record<string, string> = {
  "peer:peer-trade": "Trade",
  "peer:peer-spatial": "Spatial",
};

function Kpi({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="kpi">
      <div className="kpi-v">{value}</div>
      <div className="kpi-l">{label}</div>
      {sub && <div className="kpi-sub">{sub}</div>}
    </div>
  );
}
