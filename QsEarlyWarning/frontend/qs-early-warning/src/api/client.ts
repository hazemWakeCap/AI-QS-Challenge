// ── shared identity + selected project (set by the app; sent on every request) ──
// The backend enforces membership via RLS, so these are just "who am I / which project"; a wrong
// value yields 403/empty, never a leak. In production they come from auth + a project picker.
export const session = { userId: 1 as number, projectSlug: "" as string };

function headers(json = false): Record<string, string> {
  const h: Record<string, string> = { "X-User-Id": String(session.userId) };
  if (session.projectSlug) h["X-Project-Slug"] = session.projectSlug;
  if (json) h["Content-Type"] = "application/json";
  return h;
}

async function get<T>(url: string): Promise<T> {
  const res = await fetch(url, { headers: headers() });
  if (!res.ok) throw new Error(`${res.status} — ${await res.text()}`);
  return (await res.json()) as T;
}

async function post<T>(url: string, body?: unknown): Promise<T> {
  return send<T>("POST", url, body);
}
async function send<T>(method: string, url: string, body?: unknown): Promise<T> {
  const res = await fetch(url, { method, headers: headers(true), body: body !== undefined ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) {
    // API errors are JSON like {"error":"…"}; surface that message when present.
    let msg = text || `${res.status}`;
    try { const j = JSON.parse(text); if (j && j.error) msg = j.error; } catch { /* not JSON */ }
    throw new Error(msg);
  }
  return (text ? JSON.parse(text) : {}) as T;
}

// ── types ──
export interface Health { status: string; workbook: string; rowCount: number; centreCount: number; scorerVersion: string; featureSchemaVersion: string; forecastPeriod: number; }
export interface Project { id: number; slug: string; name: string; reportingCurrency: string; activeEstimateVersionId: number | null; ledgerActive: boolean; }
export interface EvmTotals { period: number; currency: string; bac: number; pv: number; ev: number; ac: number; cv: number; cpi: number | null; spi: number | null; eac: number; vac: number; pctBudgetConsumed: number | null; costCentres: number; amber: number; }
export interface EvmTrendPoint { period: number; pv: number; ev: number; ac: number; cpi: number | null; spi: number | null; }
export interface EvmOverview { projectSlug: string; period: number; minPeriod: number; forecastPeriod: number; totals: EvmTotals; trend: EvmTrendPoint[]; }
export interface CostCentreEvm { bccId: string; discipline: string | null; packageCode: string; lifecycle: string; alertLevel: string; bac: number; plannedPct: number | null; actualPct: number | null; pv: number; ev: number; ac: number; cpi: number | null; spi: number | null; eac: number; vac: number; pctBudgetConsumed: number | null; }
export interface Period { id: number; period: number; periodStart: string; status: string; openedAt: string | null; closedAt: string | null; }
export interface WatchlistRow { rank: number; bccId: string; discipline: string | null; packageCode: string; riskScore: number; cpi: number; gap: number; riskIndicators: string[]; }
export interface WatchlistResponse { period: number; k: number; isForecast: boolean; artifactVersion: string; trainingCutoffPeriod: number; eligibleCount: number; rows: WatchlistRow[]; }
export interface CopilotTurn { role: "user" | "assistant"; text: string; }
export interface CopilotEvidence { tool: string; detail: string; }
export interface CopilotAskResponse { answer: string; refused: boolean; evidence: CopilotEvidence[]; }
export interface FoldMetric { periodId: number; k: number; kEffective: number; eligible: number; positives: number; truePositives: number; falsePositives: number; falseNegatives: number; precision: number | null; recall: number | null; }
export interface ScorerReport { scorerLabel: string; k: number; macroPrecision: number | null; macroRecall: number | null; precisionMin: number | null; precisionMax: number | null; falseAlertsPerCycle: number; folds: FoldMetric[]; }
export interface ValidationSummary { provenance: string; scorer: string; scorerVersion: string; featureSchemaVersion: string; evaluationOriginMin: number; evaluationOriginMax: number; foldCount: number; totalTransitions: number; rule: ScorerReport[]; cpiNative: ScorerReport[]; }
export interface EntityColumn { name: string; kind: "Text" | "Numeric" | "Int" | "Bigint" | "Bool" | "Date"; insertable: boolean; updatable: boolean; required: boolean; fkEntity: string | null; enum: string[] | null; }
export interface EntityCaps { list: boolean; get: boolean; create: boolean; update: boolean; delete: boolean; }
export interface EntityMeta { key: string; display: string; table: string; naturalKey: string[]; caps: EntityCaps; columns: EntityColumn[]; }
export type EntityRow = Record<string, unknown>;

export const api = {
  health: () => get<Health>("/api/v1/health"),
  projects: () => get<Project[]>("/api/v1/projects"),
  overview: (period?: number) => get<EvmOverview>(`/api/v1/overview${period ? `?period=${period}` : ""}`),
  costCentres: (period?: number) => get<CostCentreEvm[]>(`/api/v1/cost-centres${period ? `?period=${period}` : ""}`),
  periods: () => get<Period[]>("/api/v1/periods"),
  openPeriod: (ordinal: number) => post(`/api/v1/periods/${ordinal}/open`),
  closePeriod: (ordinal: number) => post(`/api/v1/periods/${ordinal}/close`),
  captureProgress: (bccId: string, period: number, actualPct: number) =>
    post("/api/v1/capture/progress", { bccId, period, actualPct }),
  publishVersion: (versionId: number) => post(`/api/v1/estimate-versions/${versionId}/publish`),
  validationSummary: () => get<ValidationSummary>("/api/v1/validation-summary"),
  watchlist: (period: number, k: number) => get<WatchlistResponse>(`/api/v1/watchlist?period=${period}&k=${k}`),
  askCopilot: (question: string, history: CopilotTurn[]) =>
    post<CopilotAskResponse>("/api/v1/copilot/ask", { question, history }),
  // generic CRUD
  entities: () => get<EntityMeta[]>("/api/v1/entities"),
  entityList: (key: string, filters?: Record<string, string>) => {
    const q = filters && Object.keys(filters).length ? "?" + new URLSearchParams(filters).toString() : "";
    return get<EntityRow[]>(`/api/v1/entities/${key}${q}`);
  },
  entityCreate: (key: string, body: EntityRow) => post<{ id: number }>(`/api/v1/entities/${key}`, body),
  entityUpdate: (key: string, id: number, body: EntityRow) => send<{ ok: boolean }>("PUT", `/api/v1/entities/${key}/${id}`, body),
  entityDelete: (key: string, id: number) => send<{ ok: boolean }>("DELETE", `/api/v1/entities/${key}/${id}`),
};
