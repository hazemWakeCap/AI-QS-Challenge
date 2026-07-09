namespace QsEarlyWarning.Web.API.Contracts;

/// <summary>One flagged centre with its actual next-period outcome (the "we called it" reveal).</summary>
public sealed record BacktestRowDto(
    int Rank,
    string BccId,
    string? Discipline,
    string PackageCode,
    double RiskScore,
    double Cpi,
    double Gap,
    IReadOnlyList<string> RiskIndicators,
    string ActualNextAlert,
    bool Hit);

/// <summary>
/// Hindsight grade of the watchlist for one origin period, plus the model-level headline
/// (rule vs best CPI-native baseline) for the supporting "beats the baselines" bar.
/// </summary>
public sealed record BacktestResponseDto(
    int Period,
    int NextPeriod,
    int K,
    int TrainingCutoffPeriod,
    int Eligible,
    int Positives,
    int Hits,
    double? PrecisionAtK,
    IReadOnlyList<BacktestRowDto> Rows,
    // backtestable origin range for this project (drives the slider bounds)
    int OriginMin,
    int OriginMax,
    // model-level context (from the frozen validation summary)
    double? RuleMacroPrecision,
    double? BestBaselineMacroPrecision,
    string? BestBaselineLabel,
    int TotalTransitions,
    string Provenance);
