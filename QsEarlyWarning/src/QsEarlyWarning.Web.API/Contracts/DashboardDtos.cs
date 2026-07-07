namespace QsEarlyWarning.Web.API.Contracts;

/// <summary>A project the caller can select in the tenant switcher.</summary>
public sealed record ProjectDto(
    long Id, string Slug, string Name, string ReportingCurrency,
    long? ActiveEstimateVersionId, bool LedgerActive);

/// <summary>Project-level EVM totals for one reporting period (aggregated from the cost centres).</summary>
public sealed record EvmTotalsDto(
    int Period,
    string Currency,
    decimal Bac,
    decimal Pv,
    decimal Ev,
    decimal Ac,
    decimal Cv,
    double? Cpi,
    double? Spi,
    decimal Eac,
    decimal Vac,
    double? PctBudgetConsumed,
    int CostCentres,
    int Amber);

/// <summary>A point on the project's period-by-period EVM trend.</summary>
public sealed record EvmTrendPointDto(int Period, decimal Pv, decimal Ev, decimal Ac, double? Cpi, double? Spi);

public sealed record EvmOverviewDto(
    string ProjectSlug,
    int Period,
    int MinPeriod,
    int ForecastPeriod,
    EvmTotalsDto Totals,
    IReadOnlyList<EvmTrendPointDto> Trend);

public sealed record PeriodDto(long Id, int Period, DateTime PeriodStart, string Status,
                               DateTimeOffset? OpenedAt, DateTimeOffset? ClosedAt);

public sealed record CaptureProgressRequest(string BccId, int Period, decimal ActualPct);

public sealed record CaptureCostRequest(string BccId, int Period, string Rtype, decimal Amount, string Direction, string IdempotencyKey);

/// <summary>One cost centre's computed EVM for the selected period (the grid row).</summary>
public sealed record CostCentreEvmDto(
    string BccId,
    string? Discipline,
    string PackageCode,
    string Lifecycle,
    string AlertLevel,
    decimal Bac,
    double? PlannedPct,
    double? ActualPct,
    decimal Pv,
    decimal Ev,
    decimal Ac,
    double? Cpi,
    double? Spi,
    decimal Eac,
    decimal Vac,
    double? PctBudgetConsumed);
