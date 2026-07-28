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
  return parse<T>(res);
}

// multipart/form-data upload: the browser sets Content-Type (with the boundary), so we omit it here.
async function postForm<T>(url: string, form: FormData): Promise<T> {
  const res = await fetch(url, { method: "POST", headers: headers(false), body: form });
  return parse<T>(res);
}

async function parse<T>(res: Response): Promise<T> {
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
// Reconciliation summary returned by workbook import (create-from-workbook / re-import).
export interface ImportSummary { passed: boolean; activated: boolean; costCentres: number; periods: number; facts: number; failureReason: string | null; publishViolations: string[]; }
export interface EvmTotals { period: number; currency: string; bac: number; pv: number; ev: number; ac: number; cv: number; cpi: number | null; spi: number | null; eac: number; vac: number; pctBudgetConsumed: number | null; costCentres: number; amber: number; }
export interface EvmTrendPoint { period: number; pv: number; ev: number; ac: number; cpi: number | null; spi: number | null; }
export interface EvmOverview { projectSlug: string; period: number; minPeriod: number; forecastPeriod: number; totals: EvmTotals; trend: EvmTrendPoint[]; }
export interface CostCentreEvm { bccId: string; discipline: string | null; packageCode: string; lifecycle: string; alertLevel: string; bac: number; plannedPct: number | null; actualPct: number | null; pv: number; ev: number; ac: number; cpi: number | null; spi: number | null; eac: number; vac: number; pctBudgetConsumed: number | null; }
export interface Period { id: number; period: number; periodStart: string; status: string; openedAt: string | null; closedAt: string | null; }
export interface WatchlistRow { rank: number; bccId: string; discipline: string | null; packageCode: string; riskScore: number; cpi: number; gap: number; riskIndicators: string[]; }
export interface WatchlistResponse { period: number; k: number; isForecast: boolean; artifactVersion: string; trainingCutoffPeriod: number; eligibleCount: number; rows: WatchlistRow[]; }
// Proof / hindsight backtest — the watchlist graded against the actual next period
export interface BacktestRow { rank: number; bccId: string; discipline: string | null; packageCode: string; riskScore: number; cpi: number; gap: number; riskIndicators: string[]; actualNextAlert: string; hit: boolean; }
export interface BacktestResponse { period: number; nextPeriod: number; k: number; trainingCutoffPeriod: number; eligible: number; positives: number; hits: number; precisionAtK: number | null; rows: BacktestRow[]; originMin: number; originMax: number; ruleMacroPrecision: number | null; bestBaselineMacroPrecision: number | null; bestBaselineLabel: string | null; totalTransitions: number; provenance: string; }
export interface CopilotTurn { role: "user" | "assistant"; text: string; }
export interface CopilotSources { sheet: string | null; resolvedPeriod: number | null; filter: string | null; excludedCount: number | null; rowIds: string[]; }
export interface CopilotEvidence { tool: string; detail: string; sources: CopilotSources | null; }
export interface CopilotAskResponse { answer: string; refused: boolean; evidence: CopilotEvidence[]; }
export interface FoldMetric { periodId: number; k: number; kEffective: number; eligible: number; positives: number; truePositives: number; falsePositives: number; falseNegatives: number; precision: number | null; recall: number | null; }
export interface ScorerReport { scorerLabel: string; k: number; macroPrecision: number | null; macroRecall: number | null; precisionMin: number | null; precisionMax: number | null; falseAlertsPerCycle: number; folds: FoldMetric[]; }
export interface ZoneComposition { zoneArea: string; centreCount: number; disciplineCount: number; disciplines: string[]; }
/** Whether zone carries information discipline does not — the reason the spatial claim was withdrawn. */
export interface Collinearity {
  zoneCount: number; disciplineCount: number;
  singleDisciplineZones: number; disciplinesSpanningZones: number;
  zoneIsProxyForDiscipline: boolean;
  mostMixedZone: string | null; mostMixedZoneDisciplines: number;
  verdict: string; zones: ZoneComposition[];
}
export interface ValidationSummary { provenance: string; scorer: string; scorerVersion: string; featureSchemaVersion: string; evaluationOriginMin: number; evaluationOriginMax: number; foldCount: number; totalTransitions: number; rule: ScorerReport[]; cpiNative: ScorerReport[]; challenger?: ScorerReport[] | null; collinearity?: Collinearity | null; decisionsPerScorer?: number; }
export interface EntityColumn { name: string; kind: "Text" | "Numeric" | "Int" | "Bigint" | "Bool" | "Date"; insertable: boolean; updatable: boolean; required: boolean; fkEntity: string | null; enum: string[] | null; }
export interface EntityCaps { list: boolean; get: boolean; create: boolean; update: boolean; delete: boolean; }
export interface EntityMeta { key: string; display: string; table: string; naturalKey: string[]; caps: EntityCaps;
  // workbook grouping/lineage: group-level fields (group/groupLabel/groupOrder) are shared across a group and
  // drive the sheet nav; sheetRef/blurb are per-entity lineage (may differ within a group — e.g. cost-deltas).
  group: string; groupLabel: string; groupOrder: number; sheetRef: string | null; blurb: string; order: number;
  columns: EntityColumn[]; }
export type EntityRow = Record<string, unknown>;
export interface ForecastListItem { bccId: string; discipline: string | null; progressPct: number; trust: string; nextP50: number; nextP10: number | null; nextP90: number | null; nextAvailable: boolean; }
export interface HorizonBand { horizon: number; p50: number; p10: number | null; p90: number | null; available: boolean; }
export interface ConePoint { period: number; p50: number; p10: number | null; p90: number | null; }
export interface CentreForecast { bccId: string; originPeriod: number; progressPct: number; bac: number; acAtOrigin: number; trust: string; increments: HorizonBand[]; cumulativeCone: ConePoint[]; cumulativeConeAvailable: boolean; directionalFinalCost: number | null; }
export interface ProjectSpendScenario { originPeriod: number; p10: number; p50: number; p90: number; centres: number; draws: number; }
export interface HorizonMetric { predictor: string; horizon: number; n: number; maePctOfBac: number; wape: number; coverage: number | null; coverageLow: number | null; coverageHigh: number | null; fallbackCount: number; }
export interface ForecastBacktest { provenance: string; originMin: number; originMax: number; foldsEvaluated: number; foldsSkipped: number; overall: HorizonMetric[]; earlyBand: HorizonMetric[]; notes: string[]; }
// idea-3 Estimate Assumption Stress Test
export interface ReconciliationFailure { scope: string; check: string; line: string | null; actual: number; expected: number; delta: number; tolerance: number; }
export interface ReconciliationItem { scope: string; quantityReDerivationOk: boolean; resourceCostIdentityOk: boolean; repeatedContractAmtConsistent: boolean; directTieOutOk: boolean; contractUpliftOk: boolean; directTieOutDelta: number; contractUpliftDelta: number; failures: ReconciliationFailure[]; }
export interface Reconciliation { available: boolean; tiesOut: boolean; itemsChecked: number; itemsFailed: number; projectDirectDelta: number; projectUpliftDelta: number; totalDirectCost: number; totalIndirectCost: number; totalContractAmt: number; totalMargin: number; totalContingency: number; failedItems: ReconciliationItem[]; notes: string[]; }
export interface AssumptionFlag { package: string; discipline: string | null; subTrade: string | null; unit: string | null; resourceType: string | null; kind: string; severity: string; reason: string; cohortN: number; rulesVersion: string; drivingResourceLine: string | null; }
export interface PackageHeat { package: string; discipline: string | null; flagCount: number; highCount: number; severity: string; }
export interface Assumptions { available: boolean; heat: PackageHeat[]; flags: AssumptionFlag[]; notes: string[]; }
export interface PeerBenchmark { package: string; unit: string | null; resourceType: string | null; procurementRoute: string | null; subTradeAdvisory: string | null; estimatedUnitCost: number; peerMedian: number | null; peerBandLow: number | null; peerBandHigh: number | null; peerCount: number; deltaPct: number | null; status: string; }
export interface PeerBenchmarkResponse { available: boolean; retrospective: boolean; class3NoCellMeetsMinPeers: boolean; benchmarks: PeerBenchmark[]; notes: string[]; }
// idea-5 Variance Attribution Bridge
export interface ResourceContribution { resourceType: string; normShare: number; evR: number; acR: number; cvR: number; timesNormBudget: number | null; }
export interface VarianceBridge {
  bccId: string; periodId: number; available: boolean; unavailableReason: string | null;
  package: string | null; discipline: string | null;
  bac: number | null; pv: number | null; ev: number | null; ac: number | null;
  cvAed: number | null; svAed: number | null; spi: number | null;
  contributions: ResourceContribution[]; dominantResourceType: string | null;
  unexplainedResidual: number | null; tiesOut: boolean; resourceBreakdownAvailable: boolean;
  assumptionBased: boolean; evidenceNeeded: string | null; notes: string[];
}

// ── Phase 2: the spatial read-side (3D Cost X-Ray) ──
export interface ZoneCost {
  zoneCode: string;
  bac: number; pv: number; ev: number; ac: number;
  /** BAC − AC: money in this zone that has not been spent yet — what is still saveable. */
  unspent: number;
  /** ΣEV/ΣAC. Null when costSufficient is false — a ratio on 0.7% of a budget is not a verdict. */
  cpi: number | null;
  spi: number | null;
  costSufficient: boolean;
  alertLevel: "GREEN" | "AMBER" | "NOT_STARTED" | "INSUFFICIENT_COST";
  centreCount: number;
  /** AMBER centres inside the zone. Can be > 0 while the zone's own rollup reads GREEN. */
  amberCount: number;
  topRiskBccId: string | null;
  topRiskCpi: number | null;
}

export interface CostMap {
  projectSlug: string; period: number; minPeriod: number; maxPeriod: number; currency: string;
  projectBac: number; projectAc: number;
  /** Money on centres with no Zone_Area. Σ zones + this === projectBac, always. */
  unmappedBac: number;
  unmappedCentreCount: number;
  unspentInDriftingZones: number;
  zones: ZoneCost[];
}

export interface GeometryDimension {
  key: string; label: string; value: number; unit: string;
  sourceItemRef: string | null; sourceDescription: string | null; derivation: string;
}

export interface GeometrySpec {
  projectSlug: string;
  floorCount: number; basementLevels: number;
  footprintWidthM: number; footprintDepthM: number; floorHeightM: number;
  basementDepthM: number; coreWidthM: number; coreDepthM: number;
  /** False when the estimate was unavailable and fallback numbers were used. */
  derived: boolean;
  provenance: string;
  dimensions: GeometryDimension[];
}

// ── Phase 2: model take-off priced with this project's rate library ──
export interface TakeoffLineRequest {
  ifcClass: string;
  measure: "volume" | "area";
  quantity: number;
  elementCount: number;
  unmeasuredCount: number;
}

export interface PricedLine {
  ifcClass: string; measure: string; quantity: number; unit: string; elementCount: number;
  boqItemRef: string; boqDescription: string | null; unitRate: number; amount: number; rationale: string;
}

export interface UnpricedLine {
  ifcClass: string; measure: string; quantity: number; elementCount: number; reason: string;
}

export interface TakeoffRule {
  ifcClass: string; measure: string; unit: string; boqItemRef: string; rationale: string;
}

export interface TakeoffPricing {
  projectSlug: string;
  currency: string;
  /** Cost of the part that could be measured AND priced. Meaningless without `unpriced`. */
  pricedAmount: number;
  priced: PricedLine[];
  unpriced: UnpricedLine[];
  totalElements: number;
  pricedElements: number;
  unpricedElements: number;
  unmeasuredElements: number;
  /** priced + unpriced + unmeasured === totalElements. False means elements went missing. */
  tiesOut: boolean;
  rulesApplied: TakeoffRule[];
  rateBasis: string;
}

export const api = {
  health: () => get<Health>("/api/v1/health"),
  projects: () => get<Project[]>("/api/v1/projects"),
  // project lifecycle (add / manage)
  createProject: (body: { name: string; slug: string; currency: string }) => post<Project>("/api/v1/projects", body),
  importProject: (body: { name: string; slug: string; currency: string }, file: File) => {
    const f = new FormData();
    f.append("name", body.name); f.append("slug", body.slug); f.append("currency", body.currency); f.append("file", file);
    return postForm<ImportSummary>("/api/v1/projects/import", f);
  },
  reimportProject: (slug: string, file: File) => {
    const f = new FormData(); f.append("file", file);
    return postForm<ImportSummary>(`/api/v1/projects/${encodeURIComponent(slug)}/import`, f);
  },
  updateProject: (slug: string, body: { name?: string; currency?: string }) =>
    send<{ ok: boolean }>("PATCH", `/api/v1/projects/${encodeURIComponent(slug)}`, body),
  deleteProject: (slug: string) => send<{ ok: boolean }>("DELETE", `/api/v1/projects/${encodeURIComponent(slug)}`),
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
  watchlistBacktest: (period: number, k: number) => get<BacktestResponse>(`/api/v1/watchlist/backtest?period=${period}&k=${k}`),
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
  // forecast
  forecastCostCentres: () => get<ForecastListItem[]>("/api/v1/forecast/cost-centres"),
  forecastCone: (bcc: string) => get<CentreForecast>(`/api/v1/forecast/cone?bcc=${encodeURIComponent(bcc)}`),
  forecastRollup: () => get<ProjectSpendScenario>("/api/v1/forecast/rollup"),
  forecastBacktest: () => get<ForecastBacktest>("/api/v1/forecast/backtest"),
  // idea-3 stress test
  stressReconciliation: () => get<Reconciliation>("/api/v1/stress-test/reconciliation"),
  stressAssumptions: (discipline?: string) => get<Assumptions>(`/api/v1/stress-test/assumptions${discipline ? `?discipline=${encodeURIComponent(discipline)}` : ""}`),
  stressPeerBenchmark: () => get<PeerBenchmarkResponse>("/api/v1/stress-test/peer-benchmark"),
  // idea-5 variance attribution
  variance: (bcc: string, period: number) =>
    get<VarianceBridge>(`/api/v1/variance?bcc=${encodeURIComponent(bcc)}&period=${period}`),
  // phase 2: 3D cost x-ray
  costMap: (period?: number) => get<CostMap>(`/api/v1/model/cost-map${period ? `?period=${period}` : ""}`),
  geometrySpec: () => get<GeometrySpec>("/api/v1/model/geometry-spec"),
  priceTakeoff: (lines: TakeoffLineRequest[], modelElementCount: number) =>
    post<TakeoffPricing>("/api/v1/model/price-takeoff", { lines, modelElementCount }),
};
