import { type CopilotEvidence } from "../api/client";

// The "sources" panel: for every copilot tool call, the resolved filter/period, excluded-row count, and
// the source row keys — so a wrong-period or wrong-grain answer is caught in the citation. Shared by the
// Copilot chat and the Cost-Centre drawer's correction-actions block.
export function Sources({ evidence }: { evidence: CopilotEvidence[] }) {
  return (
    <details className="sources">
      <summary className="muted small">sources · {evidence.length} tool call{evidence.length === 1 ? "" : "s"}</summary>
      <ul className="sources-list">
        {evidence.map((e, j) => (
          <li key={j}>
            <span className="chip mono">{e.tool}</span>
            {e.sources?.filter && <span className="muted small"> {e.sources.filter}</span>}
            {e.sources?.excludedCount != null && e.sources.excludedCount > 0 && (
              <span className="muted small"> · {e.sources.excludedCount} rows excluded</span>
            )}
            {e.sources && e.sources.rowIds.length > 0 && (
              <div className="source-rows">
                {e.sources.sheet && <span className="muted small">{e.sources.sheet}: </span>}
                <span className="mono small">{e.sources.rowIds.slice(0, 12).join(", ")}{e.sources.rowIds.length > 12 ? ` +${e.sources.rowIds.length - 12}` : ""}</span>
              </div>
            )}
          </li>
        ))}
      </ul>
    </details>
  );
}
