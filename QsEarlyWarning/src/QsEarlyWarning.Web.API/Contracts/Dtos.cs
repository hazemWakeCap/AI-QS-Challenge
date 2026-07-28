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

/// <summary>One zone and the trades inside it — evidence for the collinearity verdict.</summary>
public sealed record ZoneCompositionDto(
    string ZoneArea, int CentreCount, int DisciplineCount, IReadOnlyList<string> Disciplines);

/// <summary>
/// Whether this project's zones carry information its disciplines do not.
///
/// <para>Published because it is the reason the spatial claim was withdrawn: if no discipline spans
/// more than one zone, a "zone-neighbour" feature measures trade, and calling its result spatial
/// would be false however good the number looked.</para>
/// </summary>
public sealed record CollinearityDto(
    int ZoneCount,
    int DisciplineCount,
    int SingleDisciplineZones,
    int DisciplinesSpanningZones,
    bool ZoneIsProxyForDiscipline,
    string? MostMixedZone,
    int MostMixedZoneDisciplines,
    string Verdict,
    IReadOnlyList<ZoneCompositionDto> Zones);

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
    IReadOnlyList<ScorerReportDto> CpiNative,
    /// <summary>Descriptive peer challengers, evaluated on identical folds. Never deployed from here.</summary>
    IReadOnlyList<ScorerReportDto>? Challenger = null,
    CollinearityDto? Collinearity = null,
    /// <summary>How many ranked slots the headline rests on (folds x k) — the honest sample size.</summary>
    int DecisionsPerScorer = 0);

public sealed record CopilotTurnDto(string Role, string Text);

public sealed record CopilotAskRequest(string Question, IReadOnlyList<CopilotTurnDto>? History);

public sealed record CopilotSourcesDto(
    string? Sheet, int? ResolvedPeriod, string? Filter, int? ExcludedCount, IReadOnlyList<string> RowIds);

public sealed record CopilotEvidenceDto(string Tool, string Detail, CopilotSourcesDto? Sources);

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
