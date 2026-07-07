namespace QsEarlyWarning.Web.API.Contracts;

public sealed record WatchlistRowDto(
    int Rank,
    string BccId,
    string? Discipline,
    string PackageCode,
    double RiskScore,
    double Cpi,
    double Gap,
    IReadOnlyList<string> RiskIndicators);

public sealed record WatchlistResponseDto(
    int Period,
    int K,
    bool IsForecast,
    string ArtifactVersion,
    int TrainingCutoffPeriod,
    int EligibleCount,
    IReadOnlyList<WatchlistRowDto> Rows);

public sealed record FoldMetricDto(
    int PeriodId, int K, int KEffective, int Eligible, int Positives,
    int TruePositives, int FalsePositives, int FalseNegatives,
    double? Precision, double? Recall);

public sealed record ScorerReportDto(
    string ScorerLabel, int K,
    double? MacroPrecision, double? MacroRecall,
    double? PrecisionMin, double? PrecisionMax,
    double FalseAlertsPerCycle,
    IReadOnlyList<FoldMetricDto> Folds);

public sealed record ValidationSummaryDto(
    string Provenance,
    string Scorer,
    string ScorerVersion,
    string FeatureSchemaVersion,
    int EvaluationOriginMin,
    int EvaluationOriginMax,
    int FoldCount,
    int TotalTransitions,
    IReadOnlyList<ScorerReportDto> Rule,
    IReadOnlyList<ScorerReportDto> CpiNative);

public sealed record CopilotTurnDto(string Role, string Text);

public sealed record CopilotAskRequest(string Question, IReadOnlyList<CopilotTurnDto>? History);

public sealed record CopilotEvidenceDto(string Tool, string Detail);

public sealed record CopilotAskResponse(
    string Answer, bool Refused, IReadOnlyList<CopilotEvidenceDto> Evidence);

public sealed record HealthDto(
    string Status,
    string Workbook,
    int RowCount,
    int CentreCount,
    string ScorerVersion,
    string FeatureSchemaVersion,
    int ForecastPeriod);
