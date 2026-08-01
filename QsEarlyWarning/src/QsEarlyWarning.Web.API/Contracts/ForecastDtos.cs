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

// ── physical-progress projection (the 4D build sequence past the last reported period) ──
// Percentages throughout, 0..100. Deliberately carries no cost figure: this projects how much of a
// centre stands, and deriving spend from it would manufacture an unvalidated final-cost number.

/// <summary>Tier is "Measured" | "Forecast" | "Extrapolated" — what stands behind this point.</summary>
public sealed record ProgressPointDto(int Period, double P50Pct, double? P10Pct, double? P90Pct, string Tier);

public sealed record CentreProgressDto(
    string BccId, int OriginPeriod, double ActualPctAtOrigin, double PacePctPerPeriod,
    int? ProjectedFinishPeriod, bool Stalled, string? AlertAtOrigin,
    IReadOnlyList<ProgressPointDto> Points);

public sealed record ProgressHorizonMetricDto(string Predictor, int Horizon, int N, double MaePp, double? Coverage);

public sealed record ProgressBandDto(int Horizon, double P10, double P90, int N);

public sealed record ProgressValidationDto(
    string Provenance, int OriginMin, int OriginMax, int Centres,
    IReadOnlyList<ProgressHorizonMetricDto> Metrics, IReadOnlyList<ProgressBandDto> Bands,
    IReadOnlyList<string> Notes);

public sealed record ProgressForecastDto(
    int OriginPeriod, int HorizonPeriod, int BacktestedThroughPeriod, int SuggestedHorizonPeriod,
    string Method, IReadOnlyList<CentreProgressDto> Centres, ProgressValidationDto Validation);

// ── projected EVM panel (the take-off tab past the last reported period) ──
// Unlike the progress projection above, this DOES carry cost — because EV is defined by the schema as
// BAC × percent complete, so projecting the percentage projects the earned value by the same arithmetic
// the database performs. AC is never derived from progress: it comes from the incremental-spend cone or
// the row reports it unavailable. PV and SPI are null past the origin; the baseline curve ends there.

/// <summary>Basis is "Measured" | "Forecast" | "Extrapolated" — what stands behind this row.</summary>
public sealed record ProjectedCentreDto(
    string BccId, int PeriodId, string Basis,
    string? Discipline, string PackageCode, string Lifecycle,
    decimal Bac,
    double PctComplete, double? PctP10, double? PctP90,
    decimal Ev, decimal? EvP10, decimal? EvP90,
    decimal? Ac, decimal? AcP10, decimal? AcP90, bool AcAvailable, string? AcNote,
    decimal? Cv, double? Cpi, decimal? Eac, decimal? Vac, double? PctBudgetConsumed,
    decimal? Pv, double? Spi, double? PlannedPct,
    string AlertLevel, bool AlertProjected,
    int? ProjectedFinishPeriod, double PacePctPerPeriod, bool Stalled);

public sealed record ProjectedPanelDto(
    int Period, int OriginPeriod, int HorizonPeriod,
    int BacktestedThroughPeriod, int SpendBacktestedThroughPeriod,
    string Basis, string Method,
    bool PvAvailable, string? PvReason,
    IReadOnlyList<string> Notes, IReadOnlyList<ProjectedCentreDto> Centres);
