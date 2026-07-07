namespace QsEarlyWarning.Web.API.Contracts;

public sealed record HorizonBandDto(int Horizon, double P50, double? P10, double? P90, bool Available);

public sealed record ConePointDto(int Period, double P50, double? P10, double? P90);

public sealed record CentreForecastDto(
    string BccId, int OriginPeriod, double ProgressPct, double Bac, double AcAtOrigin,
    string Trust, IReadOnlyList<HorizonBandDto> Increments,
    IReadOnlyList<ConePointDto> CumulativeCone, bool CumulativeConeAvailable, double? DirectionalFinalCost);

public sealed record ForecastListItemDto(
    string BccId, string? Discipline, double ProgressPct, string Trust,
    double NextP50, double? NextP10, double? NextP90, bool NextAvailable);

public sealed record ProjectSpendScenarioDto(
    int OriginPeriod, double P10, double P50, double P90, int Centres, int Draws);

public sealed record HorizonMetricDto(
    string Predictor, int Horizon, int N, double MaePctOfBac, double Wape,
    double? Coverage, double? CoverageLow, double? CoverageHigh, int FallbackCount);

public sealed record ForecastBacktestDto(
    string Provenance, int OriginMin, int OriginMax, int FoldsEvaluated, int FoldsSkipped,
    IReadOnlyList<HorizonMetricDto> Overall, IReadOnlyList<HorizonMetricDto> EarlyBand,
    IReadOnlyList<string> Notes);
