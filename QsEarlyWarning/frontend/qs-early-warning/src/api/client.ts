export interface WatchlistRow {
  rank: number;
  bccId: string;
  discipline: string | null;
  packageCode: string;
  riskScore: number;
  cpi: number;
  gap: number;
  riskIndicators: string[];
}

export interface WatchlistResponse {
  period: number;
  k: number;
  isForecast: boolean;
  artifactVersion: string;
  trainingCutoffPeriod: number;
  eligibleCount: number;
  rows: WatchlistRow[];
}

export interface FoldMetric {
  periodId: number;
  k: number;
  kEffective: number;
  eligible: number;
  positives: number;
  truePositives: number;
  falsePositives: number;
  falseNegatives: number;
  precision: number | null;
  recall: number | null;
}

export interface ScorerReport {
  scorerLabel: string;
  k: number;
  macroPrecision: number | null;
  macroRecall: number | null;
  precisionMin: number | null;
  precisionMax: number | null;
  falseAlertsPerCycle: number;
  folds: FoldMetric[];
}

export interface ValidationSummary {
  provenance: string;
  scorer: string;
  scorerVersion: string;
  featureSchemaVersion: string;
  evaluationOriginMin: number;
  evaluationOriginMax: number;
  foldCount: number;
  totalTransitions: number;
  rule: ScorerReport[];
  cpiNative: ScorerReport[];
}

export interface Health {
  status: string;
  workbook: string;
  rowCount: number;
  centreCount: number;
  scorerVersion: string;
  featureSchemaVersion: string;
  forecastPeriod: number;
}

async function get<T>(url: string): Promise<T> {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${res.statusText} — ${await res.text()}`);
  return (await res.json()) as T;
}

export const api = {
  health: () => get<Health>("/api/v1/health"),
  watchlist: (period: number, k: number) =>
    get<WatchlistResponse>(`/api/v1/watchlist?period=${period}&k=${k}`),
  validationSummary: () => get<ValidationSummary>("/api/v1/validation-summary"),
};
