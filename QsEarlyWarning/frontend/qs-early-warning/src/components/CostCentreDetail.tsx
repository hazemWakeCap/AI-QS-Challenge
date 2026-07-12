import { useEffect, useState } from "react";
import { api, type CostCentreEvm, type CopilotEvidence, type CentreForecast } from "../api/client";
import { money as fmtMoney, ratio, pct } from "../format";
import { Spinner } from "./Loading";
import { VarianceCard } from "./VarianceCard";
import { Sources } from "./Sources";
import { CorrectionForecast, type ParsedScenario } from "./CorrectionForecast";

// Per-cost-centre inspector rendered inside the right-side Drawer (mirrors the watchlist → VarianceCard
// flow). Every centre shows the KPI panel; an AMBER centre (CPI < 0.95) additionally gets the drift
// attribution (reused VarianceCard) and AI-generated correction actions.
export function CostCentreDetail({ centre, period, currency = "AED" }: { centre: CostCentreEvm; period: number; currency?: string }) {
  const money = (v: number | null | undefined) => fmtMoney(v, currency);
  const isAmber = centre.alertLevel.toUpperCase() === "AMBER";
  const cpiBad = centre.cpi != null && centre.cpi < 0.95;
  const vacBad = centre.vac != null && centre.vac < 0;

  return (
    <div>
      <div className="panel-head">
        <span className="mono">{centre.bccId}</span>
        <span className={`tag tag-${centre.alertLevel.toLowerCase().replace("_", "")}`}>{centre.alertLevel.replace("_", " ")}</span>
        <span className="muted small">{centre.discipline ?? "—"} · {centre.packageCode}</span>
      </div>

      {/* progress lane: planned vs actual % complete */}
      <div className="kpis">
        <div className="kpi"><div className="kpi-v">{money(centre.bac)}</div><div className="kpi-l">BAC (budget)</div></div>
        <div className="kpi"><div className="kpi-v">{pct(centre.plannedPct)}</div><div className="kpi-l">Plan % complete</div></div>
        <div className="kpi"><div className="kpi-v">{pct(centre.actualPct)}</div><div className="kpi-l">Actual % complete</div></div>
        <div className="kpi"><div className="kpi-v">{pct(centre.pctBudgetConsumed)}</div><div className="kpi-l">Budget consumed</div></div>
      </div>

      {/* cost + schedule lanes */}
      <div className="kpis">
        <div className="kpi"><div className="kpi-v">{money(centre.ev)}</div><div className="kpi-l">EV (earned)</div></div>
        <div className="kpi"><div className="kpi-v">{money(centre.ac)}</div><div className="kpi-l">AC (actual)</div></div>
        <div className={`kpi ${cpiBad ? "bad" : "good"}`}><div className="kpi-v">{ratio(centre.cpi)}</div><div className="kpi-l">CPI (EV ÷ AC)</div></div>
        <div className={`kpi ${centre.spi != null && centre.spi < 1 ? "bad" : ""}`}><div className="kpi-v">{ratio(centre.spi)}</div><div className="kpi-l">SPI (EV ÷ PV)</div></div>
      </div>

      {/* forecast lane */}
      <div className="kpis">
        <div className="kpi"><div className="kpi-v">{money(centre.pv)}</div><div className="kpi-l">PV (planned value)</div></div>
        <div className={`kpi ${cpiBad ? "bad" : ""}`}><div className="kpi-v">{money(centre.eac)}</div><div className="kpi-l">EAC (forecast final)</div></div>
        <div className={`kpi ${vacBad ? "bad" : "good"}`}><div className="kpi-v">{money(centre.vac)}</div><div className="kpi-l">VAC (BAC − EAC)</div></div>
        <div className="kpi"><div className="kpi-v">{centre.lifecycle}</div><div className="kpi-l">Lifecycle</div></div>
      </div>

      {isAmber ? (
        <>
          <div className="detail-section">
            <h3>What&apos;s driving the drift</h3>
            <VarianceCard bcc={centre.bccId} period={period} currency={currency} />
          </div>
          <div className="detail-section">
            <h3>Correction actions</h3>
            <CorrectionActions centre={centre} period={period} currency={currency} />
          </div>
        </>
      ) : (
        <p className="ok-msg" style={{ marginTop: 12 }}>On track — CPI ≥ 0.95, no corrective action required.</p>
      )}
    </div>
  );
}

// Pull the trailing { "scenarios": [...] } block out of the copilot answer: returns the parsed,
// validated scenarios (or null) plus the prose with that block removed for display. Tolerates a fenced
// ```json block or a bare trailing object, and strips a dangling (truncated) fence from the prose.
function parseScenarios(answer: string): { scenarios: ParsedScenario[] | null; prose: string } {
  // Prose = answer minus any ```json … ``` fence (closed or dangling) and minus a bare {"scenarios"…} tail.
  const prose = answer
    .replace(/```json[\s\S]*?```/gi, "")
    .replace(/```json[\s\S]*$/gi, "")
    .replace(/\{\s*"scenarios"[\s\S]*$/i, "")
    .trim();

  // Prefer the last closed ```json fence; else fall back to the last bare {"scenarios" …} object.
  let jsonText: string | null = null;
  const fence = /```json\s*([\s\S]*?)```/gi;
  let m: RegExpExecArray | null, lastFence: string | null = null;
  while ((m = fence.exec(answer)) !== null) lastFence = m[1];
  if (lastFence) jsonText = lastFence;
  else {
    const i = answer.lastIndexOf('"scenarios"');
    if (i >= 0) { const s = answer.lastIndexOf("{", i); if (s >= 0) jsonText = answer.slice(s); }
  }
  if (!jsonText) return { scenarios: null, prose };
  try {
    const raw = JSON.parse(jsonText);
    const arr = Array.isArray(raw?.scenarios) ? raw.scenarios : null;
    if (!arr) return { scenarios: null, prose };
    const scenarios = arr
      .filter((s: unknown): s is Record<string, unknown> => !!s && typeof s === "object")
      .map((s: Record<string, unknown>, i: number): ParsedScenario => ({
        id: typeof s.id === "string" ? s.id : `S${i + 1}`,
        title: typeof s.title === "string" && s.title.trim() ? s.title.trim() : `Action ${i + 1}`,
        overrunReductionPct: Number.isFinite(Number(s.overrunReductionPct))
          ? Math.min(1, Math.max(0, Number(s.overrunReductionPct))) : 0,
        rationale: typeof s.rationale === "string" ? s.rationale : undefined,
        evidenceNeeded: typeof s.evidenceNeeded === "string" ? s.evidenceNeeded : undefined,
      }))
      .filter((s: ParsedScenario) => s.overrunReductionPct > 0);
    return { scenarios: scenarios.length ? scenarios : null, prose };
  } catch {
    return { scenarios: null, prose };
  }
}

// AI-generated remediation. Two independent copilot calls (kept separate so neither overruns the model's
// output cap): (1) the prose actions narrated by tested tools, shown with their source trail; (2) a
// compact JSON-only estimate of each action's overrun-reduction. We pair (2) with the real forecast
// engine's no-action baseline (api.forecastCone) to draw a 3-month what-if chart + table.
function CorrectionActions({ centre, period, currency }: { centre: CostCentreEvm; period: number; currency: string }) {
  const [prose, setProse] = useState<string | null>(null);
  const [scenarios, setScenarios] = useState<ParsedScenario[] | null>(null);
  const [baseline, setBaseline] = useState<CentreForecast | null>(null);
  const [evidence, setEvidence] = useState<CopilotEvidence[]>([]);
  const [refused, setRefused] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [nonce, setNonce] = useState(0);

  const money = (v: number | null | undefined) => fmtMoney(v, currency);
  const context =
    `Cost centre ${centre.bccId} (${centre.discipline ?? "unknown discipline"}, package ${centre.packageCode}) ` +
    `is AMBER at period ${period}: CPI ${ratio(centre.cpi)}, SPI ${ratio(centre.spi)}, ` +
    `EAC ${money(centre.eac)} vs BAC ${money(centre.bac)}, budget consumed ${pct(centre.pctBudgetConsumed)} ` +
    `against ${pct(centre.actualPct)} progress. `;
  const prosePrompt = context +
    `(1) In 2–3 sentences, explain what is driving the cost drift, using the variance and drift tools. ` +
    `(2) Recommend 3–5 specific, prioritised correction actions a Quantity Surveyor should take now, ` +
    `each tied to the evidence needed to confirm the cause.`;
  const scenariosPrompt = context +
    `Using the variance and drift tools to ground your judgement, estimate the impact of the corrective ` +
    `actions available. Respond with ONLY a single fenced json code block and no prose, matching exactly ` +
    `this schema (3–5 actions):\n` +
    "```json\n" +
    `{ "scenarios": [ { "id": "S1", "title": "<=6-word action label", "overrunReductionPct": 0.35, ` +
    `"rationale": "one line", "evidenceNeeded": "what to confirm" } ] }\n` +
    "```\n" +
    `overrunReductionPct is your best estimate (0–1) of the fraction of the projected cost overrun each ` +
    `action removes once in effect. Illustrative, not a commitment.`;

  useEffect(() => {
    let cancelled = false;
    setBusy(true);
    setErr(null);
    setProse(null); setScenarios(null); setBaseline(null);
    setEvidence([]); setRefused(false);
    // Three independent fetches in parallel — prose, structured scenarios, and the forecast baseline. A
    // failure of the scenarios call or the cone just drops the what-if chart; the prose still shows.
    Promise.allSettled([
      api.askCopilot(prosePrompt, []),
      api.askCopilot(scenariosPrompt, []),
      api.forecastCone(centre.bccId),
    ])
      .then(([ask, scen, cone]) => {
        if (cancelled) return;
        if (ask.status === "fulfilled") {
          setProse(ask.value.answer);
          setEvidence(ask.value.evidence ?? []);
          setRefused(ask.value.refused);
        } else {
          setErr(String(ask.reason?.message ?? ask.reason));
        }
        if (scen.status === "fulfilled" && !scen.value.refused) {
          setScenarios(parseScenarios(scen.value.answer).scenarios);
        }
        if (cone.status === "fulfilled") setBaseline(cone.value);
      })
      .finally(() => !cancelled && setBusy(false));
    return () => { cancelled = true; };
    // Re-fires on centre/period change or an explicit Regenerate (nonce). prompt is derived from these.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [centre.bccId, period, nonce]);

  if (busy) return <Spinner label="Generating correction actions…" />;
  if (err) return (
    <div>
      <div className="error">{err}</div>
      <button className="btn btn-sm btn-secondary" style={{ marginTop: 8 }} onClick={() => setNonce((n) => n + 1)}>Retry</button>
    </div>
  );

  const canForecast = scenarios && scenarios.length > 0 && baseline && baseline.increments.length > 0;

  return (
    <div>
      <div className={`bubble assistant ${refused ? "refused" : ""}`}>
        <div className="bubble-text">{prose}</div>
        {evidence.length > 0 && <Sources evidence={evidence} />}
      </div>

      {canForecast ? (
        <CorrectionForecast baseline={baseline!} scenarios={scenarios!} currency={currency} />
      ) : !refused && (
        <p className="muted small" style={{ marginTop: 8 }}>
          {baseline && baseline.increments.length === 0
            ? "3-month forecast not available for this centre (insufficient history)."
            : "3-month forecast estimate unavailable for this run — regenerate to retry."}
        </p>
      )}

      <button className="btn btn-sm btn-secondary" style={{ marginTop: 8 }} onClick={() => setNonce((n) => n + 1)}>Regenerate</button>
    </div>
  );
}
